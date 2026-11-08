using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Storage;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LA CAPTURE DES PARTIES (decision du 14 octobre 2026). La borne filme chaque partie enregistree, avec RetroArch,
/// pendant le jeu ; le film ne part sur YouTube que si la partie est un record, sinon il est efface.
/// <list type="bullet">
/// <item>RetroArch filme l'image du coeur A SA TAILLE D'ORIGINE (decision du 15 octobre 2026 ; avant, agrandie x4), sans
/// les shaders ni les decors du joueur, avec nos reglages d'encodage (<see cref="ReglagesDuFilm"/> : x264 medium,
/// animation, CRF 14, 4:4:4). Meme fidelite que le film x4 d'avant pour 59 % de son poids et 0,27 coeur au lieu de
/// 0,69. Le film demarre et s'arrete avec le replay de la partie. <c>Video:Capture:Format = "agrandi"</c> revient au
/// film x4.</item>
/// <item>Un film a la taille d'origine ne part pas tel quel sur YouTube : pour un record, la borne fabrique d'abord sa
/// version YouTube (redressee, agrandie sans lissage, FLV) avec NelfePlay.LiveStreaming.exe, hors partie.</item>
/// <item>Un garde-fou (<see cref="GardeDeCapture"/>) coupe la capture si le jeu ralentit ; la borne ne filme
/// alors plus pendant 30 jours.</item>
/// <item>Un jeu vertical sort couche (RetroArch 1.22 filme avant de tourner l'image) : il se reconnait a l'en-tete
/// du film (<see cref="MatroskaEntete"/>). Le sens de rotation, annonce par le coeur dans le journal de lancement,
/// permet de le redresser a la fin en corrigeant l'en-tete, sans reencoder (<see cref="MatroskaRotation"/>) ;
/// YouTube l'affiche alors droit. Sans sens connu, la capture s'arrete aussitot.</item>
/// <item>Apres la partie, hors de toute partie, la borne demande au site si c'est un record : oui, elle envoie le
/// film directement chez Google ; non, elle l'efface. Sans reponse decidable, elle redemande, puis efface.</item>
/// <item>UNE PARTIE SANS REPLAY SE FILME AUSSI (19XX sous le coeur MAME, 14 octobre 2026). Sous un coeur ou
/// l'enregistreur renonce au replay (RetroArch 1.22.2 plante en encodant l'etat du coeur MAME), le film part au meme
/// depart (<c>replay.recording.skipped</c>) et s'arrete a la fin de la partie certifiee, a la baisse du score ou a la
/// fin du jeu. Il porte un identifiant a lui (<c>fm_</c>) et les sessions des scores publies pendant et juste apres
/// lui : le site y retrouve la partie et son rang. La cadence du jeu se suit alors aux trames que le plugin Lua de
/// MAME joint a ses signaux.</item>
/// </list>
/// </summary>
public sealed class ReplayCaptureService : BackgroundService
{
    private static readonly TimeSpan Releve = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PasEncoreAuPlus = TimeSpan.FromHours(3);

    /// <summary>
    /// Avant la premiere demande : le replay se finalise, le score part, le site les relie et depose le replay sur
    /// le miroir, sans quoi il ne s'affiche pas encore au classement. Quelques secondes a une minute.
    /// </summary>
    private static readonly TimeSpan PremiereDemandeApres = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModesteDurant = TimeSpan.FromDays(30);
    private const string EchelleVoulue = "4";

    /// <summary>En dessous, un film agrandi est trop petit pour YouTube : l'echelle x4 n'a pas pris.</summary>
    private const long LargeurMinimale = 512;

    /// <summary>
    /// Les reglages d'encodage du film a la taille d'origine (mesures du 15 octobre 2026, CDC video V2 §9 bis) : x264
    /// medium, tune animation, CRF 14, en 4:4:4 (la couleur de chaque pixel du jeu), son AAC a 48 kHz. Le facteur
    /// d'agrandissement de retroarch.cfg ne compte pas avec un fichier de reglages : celui-ci dit 1.
    /// </summary>
    internal const string ReglagesDuFilm =
        "vcodec = \"libx264\"\n" +
        "acodec = \"aac\"\n" +
        "pix_fmt = \"yuv444p\"\n" +
        "format = \"matroska\"\n" +
        "threads = \"2\"\n" +
        "scale_factor = \"1\"\n" +
        "sample_rate = \"48000\"\n" +
        "video_preset = \"medium\"\n" +
        "video_tune = \"animation\"\n" +
        "video_crf = \"14\"\n" +
        "audio_b = \"128k\"\n";

    /// <summary>Le fichier de ces reglages, que retroarch.cfg designe (<c>video_record_config</c>).</summary>
    internal static string CheminDesReglagesDuFilm
        => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "film-origine.cfg");

    /// <summary>Films a la taille d'origine (par defaut), ou agrandis par RetroArch comme avant le 15 octobre 2026.</summary>
    internal static bool FilmsALaTailleDOrigine(IConfiguration configuration)
        => !string.Equals(configuration.GetValue("Video:Capture:Format", "origine"), "agrandi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apres trois fabrications ratees de sa version YouTube, un film n'est plus garde.</summary>
    private const int EchecsDeVersionMax = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IEventBus _bus;
    private readonly RetroArchReplayClient _ra;
    private readonly ReplayVideoPublisher _publication;
    private readonly IReplayManifestStore _manifests;
    private readonly ReplayCoreTimingProbe _coeur;
    private readonly ILogger<ReplayCaptureService> _logger;
    private readonly bool _actif;
    private readonly bool _origine;
    private readonly double _espaceLibreMinGo;
    private readonly TimeSpan _ageMax;
    private readonly TimeSpan _intervalleEnvoi;
    private readonly long _tailleMax;
    private readonly long _tailleLocaleMax;
    private readonly string _dossier;

    /// <summary>Les films des rencontres MULTIPLAYER se gardent et partent (D-M2, 08/11) ; faux : effaces comme avant.</summary>
    private readonly bool _rencontres;
    // Les cartes du film d'une rencontre (lot M4, 08/11) : ecran de lancement VS et ecran de fin, rendus par l'API.
    private readonly RetroBat.Api.Multiplayer.MultiplayerCartesDuFilm? _cartes;

    private readonly InterfaceTextService? _textes;
    private readonly EmulationStationSettingsService? _reglagesEs;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _reveil = new(0);
    private readonly object _verrou = new();
    private CaptureEnCours? _enCours;
    private CancellationTokenSource? _envoiEnCours;
    private IDisposable? _abonnement;
    private CancellationToken _arret;

    // Le chemin du jeu lance (ui.game.started), pour le fond du cadre de son film.
    private volatile string? _cheminDuJeu;

    private sealed class CaptureEnCours
    {
        public required string ReplayId;
        public required string Fichier;
        public long FrameDebut;
        /// <summary>La rotation annoncee par le coeur (quarts de tour antihoraires), null si inconnue.</summary>
        public int? Rotation;
        /// <summary>La forme de l'image annoncee par le coeur (libretro « Aspect »), null si inconnue.</summary>
        public double? Forme;
        /// <summary>Le fanart du jeu (sinon sa capture), fond du cadre 16:9 du direct et de la video ; null sans image.</summary>
        public string? Fond;
        /// <summary>Le film du spectateur cache d'une partie MULTIPLAYER (toutes les consoles), pas celui du jeu.</summary>
        public bool DuSpectateur;
        /// <summary>Le jeu est ne sur un ecran a tube : le direct et la video en prennent l'effet CRT (29/10).</summary>
        public bool Crt;
        public bool EnteteVerifiee;
        /// <summary>Le film d'une partie sans replay : il s'arrete et se suit sans l'enregistreur.</summary>
        public bool SansReplay;
        /// <summary>D'ou viennent les trames qui suivent sa cadence (un seul emetteur, sinon les compteurs se melent).</summary>
        public string? SourceDesTrames;
        public bool Lent;
        public readonly GardeDeCapture Garde = new();
        public readonly Stopwatch Chrono = Stopwatch.StartNew();

        /// <summary>
        /// LA COURBE DU SCORE du joueur 1 (secondes du film, score atteint), pour couper la sequence du record (decision
        /// du 15 octobre 2026) : un point par quart de seconde au plus, seulement quand le score monte.
        /// </summary>
        public readonly List<double[]> Courbe = new();
        /// <summary>La fin du run certifie (continue, mort du 1LC, fin de partie), en secondes du film.</summary>
        public double? FinDuRunS;

        /// <summary>
        /// L'heure UTC du debut du film (sa creation par RetroArch) : les reperes d'une rencontre MULTIPLAYER s'y ramenent. Le
        /// film du spectateur cache commence a son lancement, bien avant que la capture le prenne (08/11).
        /// </summary>
        public DateTime DebutUtc = DateTime.UtcNow;
        /// <summary>La gameroom d'un film MULTIPLAYER (mp_), sinon null.</summary>
        public string? Room;
        /// <summary>Le vrai depart de la rencontre (multiplayer.match.started), en heure UTC.</summary>
        public DateTime? VraiDepartUtc;
        /// <summary>La fin lue de la rencontre ou de la coop (multiplayer.match.ended), en heure UTC.</summary>
        public DateTime? FinLueUtc;
        /// <summary>Le delai entre la fin lue et la fermeture du jeu (5 ou 8 s) : l'ecran de victoire du jeu.</summary>
        public double? FermetureS;
        public string? Issue;
        public bool Coop;

        public void NoterScore(long score)
        {
            lock (Courbe)
            {
                var t = Math.Round(Chrono.Elapsed.TotalSeconds, 2);
                if (Courbe.Count > 0)
                {
                    var dernier = Courbe[^1];
                    if (score <= dernier[1]) return;
                    if (t - dernier[0] < 0.25)
                    {
                        dernier[1] = score;
                        return;
                    }
                }
                Courbe.Add([t, score]);
            }
        }

        public List<double[]> CopieDeLaCourbe()
        {
            lock (Courbe) return Courbe.Select(p => (double[])p.Clone()).ToList();
        }
    }

    /// <summary>
    /// LA SEQUENCE DU RECORD (decision du 15 octobre 2026) : seule elle part sur YouTube, la partie complete se regarde
    /// avec le replay. Du moment ou le score atteint 90 % de celui de la fin du run jusqu'a cette fin, 3 minutes au plus,
    /// avec 3 s d'elan avant et 2 s apres. <paramref name="finRunS"/> : la fin du run en secondes du film (sinon la fin
    /// du film). Sans courbe, les 3 dernieres minutes. Pur.
    /// </summary>
    internal static (double DebutS, double FinS) SequenceDuRecord(IReadOnlyList<double[]>? courbe, double finRunS)
    {
        const double Max = 180, Elan = 3, Apres = 2;
        var points = (courbe ?? Array.Empty<double[]>()).Where(p => p.Length == 2 && p[0] <= finRunS + 1).ToList();
        var debut = finRunS - Max;
        if (points.Count > 0)
        {
            var seuil = points.Max(p => p[1]) * 0.9;
            debut = points.First(p => p[1] >= seuil)[0] - Elan;
        }
        debut = Math.Max(Math.Max(0, debut), finRunS - Max);
        return (Math.Round(debut, 2), Math.Round(finRunS + Apres, 2));
    }

    /// <summary>
    /// LE FILM D'UNE EPREUVE (speedrun, challenge ; 08/11). Le relevé du 08/11 : 1-1 de SMB en 4 s pour 29,77 s de run, Green
    /// Hill 1 en 15 s pour 39,78 s. <see cref="SequenceDuRecord"/> part de 90 % du score de fin, et le score d'un jeu saute a
    /// l'arrivee (drapeau et decompte du temps, bilan de fin d'acte, bilan de mission) : il ne restait que la fin. Le film
    /// d'une epreuve va du depart a l'arrivee : les bornes du run que le site designe (frames du replay, deja avec 3 s de marge
    /// de chaque cote), ramenees en secondes du film, et 2 s apres. Null sans fin connue (tout le film part) ; le debut manque,
    /// le film part de son debut. <paramref name="dureeS"/> borne la fin quand elle est connue. Pur.
    /// </summary>
    internal static (double DebutS, double FinS)? SequenceDEpreuve(long debutFilm, long? runDebut, long? runFin, double fps,
        double? dureeS)
    {
        const double Apres = 2;
        if (fps <= 1 || runFin is not long fin || fin <= debutFilm) return null;
        var debutS = runDebut is long debut && debut > debutFilm ? (debut - debutFilm) / fps : 0;
        var finS = (fin - debutFilm) / fps + Apres;
        if (dureeS is double duree && duree > 0 && finS > duree) finS = duree;
        if (finS <= debutS) return null;
        return (Math.Round(debutS, 2), Math.Round(finS, 2));
    }

    /// <summary>
    /// Une capture gardee sur la borne : en attente de la decision du site, en cours d'envoi (<paramref name="Session"/>,
    /// pour reprendre la ou l'envoi s'est arrete), puis envoyee (<paramref name="VideoId"/>) jusqu'a ce que la video
    /// soit prete chez YouTube. Le film d'une partie sans replay (<paramref name="SansReplay"/>) porte les sessions de
    /// ses scores (<paramref name="Sessions"/>), par lesquelles le site retrouve la partie. Un film a la taille d'origine
    /// (<paramref name="Origine"/>) part par sa version YouTube (<paramref name="Version"/>, fabriquee avec le sens du
    /// jeu, <paramref name="Rotation"/>, et la forme de l'image du coeur, <paramref name="Forme"/>).
    /// </summary>
    public sealed record CaptureGardee(string ReplayId, string Fichier, long DebutFilm, long FinFilm, DateTime CreeeUtc,
        string? Session = null, string? VideoId = null, DateTime? EnvoyeeUtc = null,
        IReadOnlyList<string>? Sessions = null, bool SansReplay = false,
        bool Origine = false, int? Rotation = null, string? Version = null, int EchecsDeVersion = 0,
        IReadOnlyList<double[]>? Courbe = null, double? FinDuRunS = null, double? DureeS = null,
        double? DebutSequenceS = null, double? FinSequenceS = null, double? Forme = null,
        int Largeur = 0, int Hauteur = 0, string? Fond = null, bool Crt = false,
        string? Room = null, DateTime? DebutUtc = null, double? VraiDepartS = null, double? FinLueS = null,
        bool RencontreComplete = false, bool Coop = false, string? Issue = null, bool DuSpectateur = false,
        string? CarteAvant = null, double CarteAvantS = 0, string? CarteApres = null, double CarteApresS = 0);

    /// <summary>
    /// LA PORTION D'UNE RENCONTRE MULTIPLAYER (CDC films MULTIPLAYER, 08/11), en secondes du film : du vrai depart moins
    /// <see cref="ElanDeLaRencontreS"/> (la boucle du depart pret d'avant ne part pas) a la fin lue plus le delai de fermeture
    /// (l'ecran de victoire du jeu), bornee par la fin du film. <paramref name="Complete"/> : le vrai depart et la fin lue sont
    /// tous deux dans le film.
    /// </summary>
    public sealed record PortionDeRencontre(double DebutS, double FinS, bool Complete)
    {
        public double DureeS => Math.Round(FinS - DebutS, 2);
    }

    /// <summary>Le film d'une rencontre commence 2 s avant son vrai depart.</summary>
    internal const double ElanDeLaRencontreS = 2;

    /// <summary>Sans fin lue (jeu libre, partie fermee, invites partis), le film s'arrete 1 s avant sa fin.</summary>
    internal const double FinSansIssueS = 1;

    /// <summary>Une rencontre plus longue ne part pas (decision D-M3 du 08/11 : 6 minutes au plus).</summary>
    internal const double DureeMaxDUneRencontreS = 360;

    /// <summary>
    /// La portion d'une rencontre (<see cref="PortionDeRencontre"/>), ou null et la raison. Sans vrai depart (invite jamais
    /// arrive, partie a un seul humain), ou un vrai depart avant le debut du film : pas de film. Une portion incomplete (sans
    /// fin lue) de moins de 20 s non plus ; une portion de plus de 6 minutes non plus. Les reperes sont en secondes du film.
    /// Pur.
    /// </summary>
    internal static (PortionDeRencontre? Portion, string Raison) PortionDeLaRencontre(double? vraiDepartS, double? finLueS,
        double? fermetureS, double dureeS)
    {
        if (dureeS <= 0) return (null, "film vide");
        if (vraiDepartS is not double depart) return (null, "sans vrai depart");
        if (depart < 0) return (null, "le film commence apres le vrai depart");
        if (depart >= dureeS) return (null, "vrai depart apres la fin du film");
        var debut = Math.Max(0, depart - ElanDeLaRencontreS);
        double fin;
        var complete = false;
        if (finLueS is double finLue && finLue > depart && finLue <= dureeS)
        {
            fin = Math.Min(dureeS, finLue + Math.Max(0, fermetureS ?? 5));
            complete = true;
        }
        else
        {
            fin = dureeS - FinSansIssueS;
        }
        if (fin <= debut) return (null, "portion vide");
        var duree = fin - debut;
        if (!complete && duree < ReplayVideoPublisher.DureeMinimaleS)
            return (null, "moins de " + ReplayVideoPublisher.DureeMinimaleS + " s sans fin lue");
        if (duree > DureeMaxDUneRencontreS) return (null, "plus de " + DureeMaxDUneRencontreS / 60 + " min");
        return (new PortionDeRencontre(Math.Round(debut, 2), Math.Round(fin, 2), complete), "rencontre");
    }

    /// <summary>Un instant UTC en secondes du film qui commence a <paramref name="debutDuFilm"/> ; null sans instant. Pur.</summary>
    internal static double? SecondesDuFilm(DateTime debutDuFilm, DateTime? instant)
        => instant is DateTime t ? Math.Round((t - debutDuFilm).TotalSeconds, 2) : null;

    /// <summary>
    /// Le debut du film a l'heure UTC : sa creation, si elle est plausible (dans les 10 dernieres minutes, pas dans le futur),
    /// sinon <paramref name="maintenant"/>. Pur.
    /// </summary>
    internal static DateTime DebutDuFilm(DateTime? creeUtc, DateTime maintenant)
        => creeUtc is DateTime cree && cree <= maintenant && maintenant - cree < TimeSpan.FromMinutes(10) ? cree : maintenant;

    /// <summary>
    /// LE GARDE-FOU DE LA FORME (decision du user du 25 octobre 2026) : un film dont l'image n'a pas la forme du jeu ne part
    /// pas sur YouTube. Sans forme connue du coeur, une image deux fois plus large que haute (ou l'inverse) est refusee ;
    /// avec elle, un ecart de 15 % ou plus demande un exe qui sait la rendre (--forme, 0.5.8). Un jeu tourne garde son
    /// redressement d'avant. La raison du refus, ou null. Pur.
    /// </summary>
    internal static string? FormeRefusee(int largeur, int hauteur, int? rotation, double? forme, bool exeSaitLaForme)
    {
        if (largeur <= 0 || hauteur <= 0 || rotation is 1 or 2 or 3) return null;
        var rapport = (double)largeur / hauteur;
        if (forme is not double a)
            return rapport is > 2.0 or < 0.5 ? $"image {largeur}x{hauteur} sans forme connue du coeur" : null;
        if (Math.Abs(rapport - a) / a >= 0.15 && !exeSaitLaForme)
            return $"image {largeur}x{hauteur} a la forme {a:0.###}, exe du direct trop ancien pour la rendre";
        return null;
    }

    /// <summary>L'exe du direct sait-il rendre la forme du coeur (--forme, a partir de 0.5.8) ?</summary>
    internal static bool ExeSaitLaForme(string? exe)
    {
        if (exe is null) return false;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) >= new Version(0, 5, 8);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Le message RetroArch d'un filtre d'image, si le Data Pack ne l'a pas encore (meme texte en anglais).</summary>
    internal const string MessageDuFiltreParDefaut =
        "Image filter (NTSC) on: no live or video of this game. A record video will be made from the replay, without the filter.";

    /// <summary>
    /// PREVENIR DU FILTRE AU LANCEMENT (decision du user du 25 octobre 2026) : un message de RetroArch, la partie pas
    /// encore commencee (le direct d'une partie NelfePlay s'arme au chargement du jeu). Trois fois, RetroArch ne le
    /// tient que trois secondes.
    /// </summary>
    public async Task PrevenirDuFiltreAsync(CancellationToken ct)
    {
        string langue;
        try { langue = _reglagesEs?.GetScrapingSettings().Language ?? "en"; } catch (Exception) { langue = "en"; }
        var texte = _textes?.Text("video.filter.no_live", langue) is { Length: > 0 } t && t != "video.filter.no_live"
            ? t : MessageDuFiltreParDefaut;
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            await _ra.ShowMessageAsync(texte, ct).ConfigureAwait(false);
            await Task.Delay(3200, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Le filtre d'image actif du coeur charge (<see cref="FiltreDImage"/>), « cle = valeur », ou null : la partie ne se
    /// filme pas, son direct ne part pas.
    /// </summary>
    public string? FiltreActif() => FiltreActif(null);

    /// <summary>
    /// Le filtre d'image actif, une fois le jeu charge dans RetroArch (le journal parle alors de lui) ; null apres
    /// <paramref name="attente"/> sans jeu charge.
    /// </summary>
    public async Task<string?> FiltreActifAuChargementAsync(TimeSpan attente, CancellationToken ct)
    {
        var limite = DateTime.UtcNow + attente;
        while (DateTime.UtcNow < limite && !ct.IsCancellationRequested)
        {
            if (await _ra.GetStatusAsync(ct).ConfigureAwait(false) is { ContentLoaded: true } statut)
                return FiltreActif(statut.Crc32);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Le filtre actif ; <paramref name="crc32"/> donne, le journal doit parler de ce contenu-la.</summary>
    private string? FiltreActif(string? crc32)
    {
        try
        {
            var cadence = _coeur.ReadLatest();
            if (crc32 is { Length: > 0 } crc && cadence?.Crc32 is { } duJournal
                && !string.Equals(duJournal, crc.Trim().ToLowerInvariant().PadLeft(8, '0'), StringComparison.Ordinal))
                return null;
            var coeur = cadence?.Coeur;
            if (coeur is null) return null;
            var reglages = LireRetroArch();
            var fichier = FiltreDImage.FichierDesOptions(RetroBatPaths.RetroArchConfigPath, Valeur(reglages, "core_options_path"));
            return File.Exists(fichier) ? FiltreDImage.Actif(coeur, File.ReadLines(fichier)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture : options du coeur illisibles.");
            return null;
        }
    }

    /// <summary>
    /// Les frames du replay que la video couvre : la sequence du record quand elle a ete coupee, sinon tout le film
    /// (<paramref name="finFilm"/>). Le site en tire le debut et la fin de chaque performance dans la video. Pur.
    /// </summary>
    internal static (long Debut, long Fin) PortionDuReplay(CaptureGardee g, long finFilm, double fps)
        => g.DebutSequenceS is double debut && g.FinSequenceS is double fin && fps > 1
            ? (g.DebutFilm + (long)Math.Round(debut * fps), Math.Min(finFilm, g.DebutFilm + (long)Math.Round(fin * fps)))
            : (g.DebutFilm, finFilm);

    /// <summary>Le prefixe des films sans replay : le site les reconnait et les relie par les sessions des scores.</summary>
    internal const string PrefixeSansReplay = "fm_";

    /// <summary>
    /// Le prefixe du film d'une partie MULTIPLAYER (27/10) : il ne sert qu'au direct partage, la borne l'efface apres la
    /// fin de passage.
    /// </summary>
    internal const string PrefixeMultiplayer = "mp_";

    /// <summary>Le film MULTIPLAYER reste le temps que l'exe du direct joue sa fin de passage (deux minutes au plus).</summary>
    private static readonly TimeSpan GardeDuFilmMultiplayer = TimeSpan.FromMinutes(5);

    internal static bool EstUnFilmMultiplayer(string replayId) => replayId.StartsWith(PrefixeMultiplayer, StringComparison.Ordinal);

    /// <summary>Les verdicts arrives juste apres un film sans replay sont les siens (la partie vient de finir).</summary>
    private static readonly TimeSpan SessionsTardivesPendant = TimeSpan.FromMinutes(10);

    private readonly object _verrouSessions = new();
    private readonly Dictionary<string, HashSet<string>> _sessionsDesFilms = new(StringComparer.Ordinal);
    private (string Id, DateTime Fin)? _dernierSansReplay;

    /// <summary>Une video envoyee qui n'est toujours pas prete apres ce delai : le film n'est plus garde.</summary>
    private static readonly TimeSpan PreteAuPlus = TimeSpan.FromHours(24);

    public ReplayCaptureService(IEventBus bus, RetroArchReplayClient ra, ReplayVideoPublisher publication,
        IReplayManifestStore manifests, IReplayObjectStore objects, ReplayCoreTimingProbe coeur, IConfiguration configuration,
        ILogger<ReplayCaptureService> logger, InterfaceTextService? textes = null, EmulationStationSettingsService? reglagesEs = null,
        RetroBat.Api.Multiplayer.MultiplayerCartesDuFilm? cartes = null)
    {
        _cartes = cartes;
        _bus = bus; _ra = ra; _publication = publication; _manifests = manifests; _coeur = coeur; _logger = logger;
        _textes = textes; _reglagesEs = reglagesEs;
        _actif = configuration.GetValue("Video:Capture:Enabled", true);
        _origine = FilmsALaTailleDOrigine(configuration);
        _espaceLibreMinGo = Math.Max(1, configuration.GetValue("Video:Capture:MinFreeGb", 5.0));
        _ageMax = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Video:Capture:MaxAgeHours", 48)));
        _intervalleEnvoi = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Capture:UploadIntervalMinutes", 2)));
        // Le plus gros film ENVOYE (300 Mo, le plafond du site). Jamais plusieurs Go demandes au joueur (decision du
        // 15 octobre 2026 : certains paient leur envoi) ; seule la sequence du record doit partir. Le film sur le disque,
        // lui, continue au-dela : le direct d'un record tombe tard dans une longue partie, et il est la copie du film.
        _tailleMax = Math.Max(1, configuration.GetValue("Video:Capture:MaxMb", 300)) * 1_000_000L;
        _tailleLocaleMax = Math.Max(_tailleMax / 1_000_000L, configuration.GetValue("Video:Capture:MaxLocalMb", 4000L)) * 1_000_000L;
        _dossier = Path.GetFullPath(Path.Combine(objects.TempRoot, "..", "captures"));
        _rencontres = configuration.GetValue("Video:Capture:Multiplayer", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _arret = stoppingToken;
        if (!_actif)
        {
            _logger.LogInformation("Capture : desactivee (Video:Capture:Enabled).");
            return;
        }
        try
        {
            Directory.CreateDirectory(_dossier);
            PreparerRetroArch();
            _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : demarrage impossible, aucune partie ne sera filmee.");
            return;
        }
        _logger.LogInformation("Capture : active, films dans {Dossier}.", _dossier);

        var prochainEnvoi = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reveille = await _reveil.WaitAsync(Releve, stoppingToken).ConfigureAwait(false);
                await SurveillerAsync(stoppingToken).ConfigureAwait(false);
                if (reveille || DateTime.UtcNow >= prochainEnvoi)
                {
                    prochainEnvoi = DateTime.UtcNow + _intervalleEnvoi;
                    await EnvoyerLesCapturesAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // L'hote s'arrete sur toute exception d'un service de fond (StopHost) : un tour rate ne doit
                // jamais emporter l'API.
                _logger.LogWarning(ex, "Capture : tour en echec.");
            }
        }
    }

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "replay.recording.started":
                    if (Chaine(e.Payload, "ReplayId") is { } demarre) _ = Task.Run(() => DemarrerAsync(demarre));
                    break;
                case "replay.recording.stopped":
                    if (Chaine(e.Payload, "ReplayId") is { } arrete)
                        _ = Task.Run(() => ArreterAsync(arrete, Entier(e.Payload, "LastFrame"), abandon: false, "fin du replay"));
                    break;
                // Une partie RetroArch sans replay (coeur MAME) : le film part quand meme, et s'arrete sans l'enregistreur.
                case "replay.recording.skipped":
                    _ = Task.Run(() => DemarrerAsync(PrefixeSansReplay + Ulid.New(), sansReplay: true));
                    break;
                // La borne qui heberge une partie MULTIPLAYER la filme pour le direct (aucun replay : pas de score).
                case RetroBat.Api.Multiplayer.MultiplayerLaunchService.EvenementPartieDeLHote:
                    var salle = Chaine(e.Payload, "Room");
                    _ = Task.Run(() => FilmerLaPartieMultiplayerAsync(salle));
                    break;
                // LES REPERES DE LA RENCONTRE (CDC films MULTIPLAYER, 08/11) : le vrai depart puis la fin lue, dans le film
                // MULTIPLAYER en cours. Un vrai depart arrive sans film ne donne pas de film (rien a couper).
                case RetroBat.Api.Multiplayer.MultiplayerLaunchService.EvenementRencontreCommencee:
                    if (_enCours is { } debutMp && EstUnFilmMultiplayer(debutMp.ReplayId) && MemeSalle(debutMp.Room, Chaine(e.Payload, "Room")))
                        debutMp.VraiDepartUtc ??= Instant(e.Payload, "Utc") ?? DateTime.UtcNow;
                    break;
                case RetroBat.Api.Multiplayer.MultiplayerLaunchService.EvenementRencontreFinie:
                    if (_enCours is { } finMp && EstUnFilmMultiplayer(finMp.ReplayId) && MemeSalle(finMp.Room, Chaine(e.Payload, "Room"))
                        && finMp.FinLueUtc is null)
                    {
                        finMp.FermetureS = Entier(e.Payload, "FermetureS") ?? 5;
                        finMp.Issue = Chaine(e.Payload, "Issue");
                        finMp.Coop = Booleen(e.Payload, "Coop") ?? false;
                        finMp.FinLueUtc = Instant(e.Payload, "Utc") ?? DateTime.UtcNow;
                    }
                    break;
                case "score.live.changed":
                    // La courbe du score du joueur 1, pour la sequence du record.
                    if (_enCours is { } filme && Entier(e.Payload, "Score") is { } valeur && (Entier(e.Payload, "Player") ?? 1) == 1)
                        filme.NoterScore(valeur);
                    break;
                case "scoring.run.ended":
                case "scoring.replay.stop":
                    if (e.Type == "scoring.run.ended" && _enCours is { } finDuRun)
                        finDuRun.FinDuRunS ??= Math.Round(finDuRun.Chrono.Elapsed.TotalSeconds, 2);
                    if (_enCours is { SansReplay: true } sansReplay && !EstUnFilmMultiplayer(sansReplay.ReplayId))
                        _ = Task.Run(() => ArreterAsync(sansReplay.ReplayId, null, abandon: false,
                            e.Type == "scoring.run.ended" ? "fin de la partie certifiee" : "le score a baisse"));
                    break;
                case "scoring.verdict":
                    NoterSession(Chaine(e.Payload, "SessionId"));
                    break;
                case "ingame.memory.changed":
                    if (_enCours is { SansReplay: true } suivi && TrameDuJeu(e.Payload) is { } trame)
                        SuivreLaCadence(suivi, trame.Source, trame.Frame);
                    break;
                case "ui.game.ended":
                    _ = Task.Run(() => ArreterAsync(null, null, abandon: false, "fin de partie"));
                    break;
                case "ui.game.started":
                    // Le jeu lance, pour le fond du cadre de son film (fanart).
                    _cheminDuJeu = Chaine(e.Payload, "GamePath");
                    // Le joueur passe avant : un envoi en cours s'arrete, il reprendra apres la partie.
                    lock (_verrou)
                    {
                        try { _envoiEnCours?.Cancel(); } catch (Exception) { /* deja termine */ }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture : evenement {Type} ignore.", e.Type);
        }
    }

    /// <summary>
    /// Le replay de la partie demarre : la capture aussi, si la borne le peut. <paramref name="sansReplay"/> : la partie
    /// part sans replay, le film porte alors son propre identifiant.
    /// </summary>
    private async Task DemarrerAsync(string replayId, bool sansReplay = false, bool duSpectateur = false, string? room = null)
    {
        await _operation.WaitAsync(_arret).ConfigureAwait(false);
        try
        {
            if (_enCours is not null || EstModeste()) return;
            var reglages = LireRetroArch();
            if (string.Equals(Valeur(reglages, "video_gpu_record"), "true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Capture : RetroArch filme l'image affichee (video_gpu_record), partie non filmee.");
                return;
            }
            // Un filtre d'image du coeur (NTSC) ne se diffuse pas bien (25/10) : ni film ni direct ; la video d'un record
            // se fera depuis son replay, rendu sans les reglages du joueur.
            if (FiltreActif() is { } filtre)
            {
                _logger.LogInformation("Capture : filtre d'image du coeur actif ({Filtre}), partie non filmee.", filtre);
                return;
            }
            // Le spectateur cache d'une partie MULTIPLAYER filme dans son propre dossier (MultiplayerSpectateur).
            var dossier = duSpectateur ? RetroBat.Api.Multiplayer.MultiplayerSpectateur.DossierDesFilms : Valeur(reglages, "recording_output_directory");
            if (string.IsNullOrWhiteSpace(dossier))
            {
                _logger.LogInformation("Capture : aucun dossier de films dans retroarch.cfg, partie non filmee.");
                return;
            }
            Directory.CreateDirectory(dossier);
            if (EspaceLibreGo(dossier) is { } libre && libre < _espaceLibreMinGo)
            {
                _logger.LogInformation("Capture : {Libre:F1} Go libres, partie non filmee.", libre);
                return;
            }

            // Le spectateur cache filme depuis son lancement (sans port de commandes, MultiplayerSpectateur) : son film est la.
            string? fichier = duSpectateur && RetroBat.Api.Multiplayer.MultiplayerSpectateur.Film is { } film && File.Exists(film) ? film : null;
            var frame = 0L;
            if (!duSpectateur)
            {
                var avant = new HashSet<string>(Directory.GetFiles(dossier, "*.mkv"), StringComparer.OrdinalIgnoreCase);
                await _ra.RecordingToggleAsync(_arret).ConfigureAwait(false);
                frame = (await _ra.GetActiveReplayAsync(_arret).ConfigureAwait(false))?.Frame ?? 0;
                for (var i = 0; i < 25 && fichier is null; i++)
                {
                    await Task.Delay(200, _arret).ConfigureAwait(false);
                    fichier = Directory.GetFiles(dossier, "*.mkv").Where(f => !avant.Contains(f))
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                }
            }
            if (fichier is null)
            {
                _logger.LogInformation("Capture : RetroArch n'a pas commence a filmer {ReplayId}.", replayId);
                return;
            }
            var statut = duSpectateur ? null : await _ra.GetStatusAsync(_arret).ConfigureAwait(false);
            var rotation = duSpectateur ? 0 : _coeur.ReadRotation(statut?.Crc32);
            // La forme de l'image du coeur, si le journal parle bien de ce contenu. Jamais pour le spectateur : la forme
            // annoncee est celle d'UNE console (3:2), son image en montre deux ou quatre.
            var cadence = duSpectateur ? null : _coeur.ReadLatest();
            var forme = cadence is { Aspect: double a } && (statut?.Crc32 is not { Length: > 0 } crc || cadence.Crc32 is null
                || string.Equals(cadence.Crc32, crc.Trim().ToLowerInvariant().PadLeft(8, '0'), StringComparison.Ordinal)) ? a : (double?)null;
            lock (_verrouSessions)
            {
                // Une autre partie commence : les verdicts qui suivent ne sont plus ceux du film d'avant.
                _dernierSansReplay = null;
                _enCours = new CaptureEnCours
                {
                    ReplayId = replayId, Fichier = fichier, FrameDebut = frame, Rotation = rotation, SansReplay = sansReplay,
                    Forme = forme, Fond = FondDuJeu(_cheminDuJeu), DuSpectateur = duSpectateur,
                    Crt = LanceurDuDirect.EcranATube(_cheminDuJeu is { Length: > 0 } cheminDuJeu
                        ? RetroBat.Api.Netplay.NetplayHostService.SystemeDuChemin(cheminDuJeu, RetroBatPaths.RomsRoot) : null),
                    // Le film d'une rencontre : sa gameroom, et l'heure de sa premiere image (le spectateur filme depuis son
                    // lancement, la capture le prend plus tard).
                    Room = room,
                    DebutUtc = EstUnFilmMultiplayer(replayId) ? DebutDuFilm(CreationUtc(fichier), DateTime.UtcNow) : DateTime.UtcNow,
                };
            }
            // Le spectateur cache est filme : l'hote peut donner sa session aux invites (D-M9).
            if (duSpectateur) RetroBat.Api.Multiplayer.MultiplayerSpectateur.NoterFilme();
            if (sansReplay)
                _logger.LogInformation("Capture : partie sans replay filmee sous {ReplayId} (rotation du coeur : {Rotation}).",
                    replayId, rotation?.ToString() ?? "inconnue");
            else
                _logger.LogInformation("Capture : partie {ReplayId} filmee depuis la frame {Frame} (rotation du coeur : {Rotation}).",
                    replayId, frame, rotation?.ToString() ?? "inconnue");
        }
        catch (OperationCanceledException) { /* arret de l'API */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : demarrage de {ReplayId} en echec.", replayId);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// L'emulateur de la partie MULTIPLAYER vient de paraitre : le film part quand le jeu est charge (un RECORDING_TOGGLE
    /// envoye pendant le chargement se perd). Sans GET_STATUS sur ce coeur (GardeGetStatus), il part apres quelques
    /// secondes.
    /// </summary>
    /// <summary>L'attente du spectateur cache : l'arrivee des invites (le depart les attend 150 s au plus), puis la sienne.</summary>
    private const int AttenteDuSpectateurS = 240;

    private async Task FilmerLaPartieMultiplayerAsync(string? room)
    {
        try
        {
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(750, _arret).ConfigureAwait(false);
                if (!GardeGetStatus.SansDanger())
                {
                    if (i >= 6) break;
                    continue;
                }
                if ((await _ra.GetStatusAsync(_arret).ConfigureAwait(false))?.ContentLoaded == true) break;
            }
            // LE SPECTATEUR CACHE (28/10) : une partie de consoles reliees se filme chez lui (toutes les consoles), des qu'il a
            // rejoint la partie ; il arrive apres les invites. Sans lui, l'hote filme sa propre image, comme avant.
            for (var i = 0; i < AttenteDuSpectateurS && RetroBat.Api.Multiplayer.MultiplayerSpectateur.Prevu
                            && !RetroBat.Api.Multiplayer.MultiplayerSpectateur.Connecte; i++)
            {
                await Task.Delay(1000, _arret).ConfigureAwait(false);
            }
            var duSpectateur = RetroBat.Api.Multiplayer.MultiplayerSpectateur.Connecte;
            if (duSpectateur) await Task.Delay(3000, _arret).ConfigureAwait(false);
            await DemarrerAsync(PrefixeMultiplayer + Ulid.New(), sansReplay: true, duSpectateur: duSpectateur, room: room).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* arret de l'API */ }
    }

    /// <summary>
    /// Arrete la capture en cours : RetroArch ferme le film, la borne le range (ou l'efface s'il est
    /// <paramref name="abandon"/>ne). <paramref name="replayId"/> null : quelle que soit la capture.
    /// </summary>
    private async Task ArreterAsync(string? replayId, long? derniereFrame, bool abandon, string pourquoi)
    {
        await _operation.WaitAsync(_arret).ConfigureAwait(false);
        try
        {
            var c = _enCours;
            if (c is null || (replayId is not null && replayId != c.ReplayId)) return;
            // La duree du film au moment de l'arret, avant les attentes qui suivent.
            var dureeAuStop = Math.Round(c.Chrono.Elapsed.TotalSeconds, 2);
            lock (_verrouSessions)
            {
                _enCours = null;
                // Le verdict du 1CC arrive juste apres la fin de la partie : il est encore a ce film.
                if (c.SansReplay && !abandon && !EstUnFilmMultiplayer(c.ReplayId)) _dernierSansReplay = (c.ReplayId, DateTime.UtcNow);
            }

            // Un film qui ne grossit plus est deja ferme (RetroArch quitte) : un RECORDING_TOGGLE en
            // relancerait un autre.
            if (await GrossitAsync(c.Fichier).ConfigureAwait(false))
            {
                // Le spectateur termine son film en quittant (sa fenetre fermee).
                if (c.DuSpectateur) await RetroBat.Api.Multiplayer.MultiplayerSpectateur.FermerAsync(_arret).ConfigureAwait(false);
                else await _ra.RecordingToggleAsync(_arret).ConfigureAwait(false);
            }
            await AttendreLaFinAsync(c.Fichier).ConfigureAwait(false);

            if (EstUnFilmMultiplayer(c.ReplayId) && !abandon)
            {
                // LE FILM DE LA RENCONTRE (CDC films MULTIPLAYER, 08/11) : une rencontre a vrai depart se garde, portion notee ;
                // elle partira hors partie si le site la veut. La duree du film va de sa premiere image a sa derniere ecriture
                // (RetroArch ferme, le lanceur rend la main jusqu'a dix secondes plus tard).
                var finDuFilm = DerniereEcritureUtc(c.Fichier) is DateTime ecrit && ecrit < DateTime.UtcNow ? ecrit : DateTime.UtcNow;
                var dureeDuFilmS = Math.Round((finDuFilm - c.DebutUtc).TotalSeconds, 2);
                var (portion, raison) = PortionDeLaRencontre(SecondesDuFilm(c.DebutUtc, c.VraiDepartUtc),
                    SecondesDuFilm(c.DebutUtc, c.FinLueUtc), c.FermetureS, dureeDuFilmS);
                if (portion is not null && _rencontres)
                {
                    _ = Task.Run(() => GarderLaRencontreAsync(c, portion, dureeDuFilmS));
                    return;
                }
                // Aucun site ne le demandera : il s'efface une fois la fin de passage jouee.
                var film = c.Fichier;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(GardeDuFilmMultiplayer, _arret).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* arret de l'API : efface tout de suite */ }
                    Effacer(film);
                });
                _logger.LogInformation("Capture : partie MULTIPLAYER {ReplayId} finie ({Raison}), film efface dans {Minutes} min.", c.ReplayId,
                    _rencontres ? raison : "films des rencontres coupes", GardeDuFilmMultiplayer.TotalMinutes);
                return;
            }

            if (abandon)
            {
                Effacer(c.Fichier);
                OublierLesSessions(c.ReplayId);
                _logger.LogInformation("Capture : {ReplayId} abandonnee ({Pourquoi}), film efface.", c.ReplayId, pourquoi);
                return;
            }
            var entete = MatroskaEntete.Lire(c.Fichier);
            var origine = entete is { Origine: true };
            // Jeu vertical (ou retourne) : un film agrandi par RetroArch se redresse dans son en-tete, sans toucher aux
            // images ; un film a la taille d'origine se redresse dans sa version YouTube.
            if (!origine && c.Rotation is 1 or 2 or 3)
            {
                if (MatroskaRotation.Redresser(c.Fichier, c.Rotation.Value))
                    _logger.LogInformation("Capture : {ReplayId} redressee (rotation {Rotation} du coeur).", c.ReplayId, c.Rotation);
                else
                    _logger.LogInformation("Capture : {ReplayId}, rotation non inscrite dans le film.", c.ReplayId);
                entete = MatroskaEntete.Lire(c.Fichier);
            }
            if (entete is null || (entete.Couchee && !(origine && c.Rotation is 1 or 3)) || (!origine && entete.Largeur < LargeurMinimale))
            {
                Effacer(c.Fichier);
                OublierLesSessions(c.ReplayId);
                _logger.LogInformation("Capture : {ReplayId} non gardee ({Raison}).", c.ReplayId,
                    entete is null ? "film illisible" : entete.Couchee ? "jeu vertical, film couche" : "image trop petite");
                return;
            }

            var destination = Path.Combine(_dossier, c.ReplayId + ".mkv");
            Effacer(destination);
            File.Move(c.Fichier, destination);
            var gardee = new CaptureGardee(c.ReplayId, destination, c.FrameDebut,
                Math.Max(c.FrameDebut, derniereFrame ?? 0), DateTime.UtcNow,
                Sessions: c.SansReplay ? SessionsDuFilm(c.ReplayId, null) : null, SansReplay: c.SansReplay,
                Origine: origine, Rotation: c.Rotation,
                Courbe: c.CopieDeLaCourbe(), FinDuRunS: c.FinDuRunS, DureeS: dureeAuStop,
                Forme: c.Forme, Largeur: (int)entete.Largeur, Hauteur: (int)entete.Hauteur, Fond: c.Fond, Crt: c.Crt);
            await File.WriteAllTextAsync(Path.Combine(_dossier, c.ReplayId + ".json"),
                JsonSerializer.Serialize(gardee, Json), Encoding.UTF8, _arret).ConfigureAwait(false);
            _logger.LogInformation("Capture : {ReplayId} gardee ({Taille} octets, {Format}, {Pourquoi}), en attente de la decision du site.",
                c.ReplayId, new FileInfo(destination).Length,
                origine ? "taille d'origine " + entete.Largeur + "x" + entete.Hauteur : "agrandi " + entete.Largeur + "x" + entete.Hauteur, pourquoi);
            _reveil.Release();
        }
        catch (OperationCanceledException) { /* arret de l'API */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : arret en echec.");
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Le film en cours d'ecriture et la rotation du coeur, pour le direct d'un record (le direct en est la copie) ;
    /// null quand rien n'est filme.
    /// </summary>
    public (string Fichier, int? Rotation, double? Forme, string? Fond, bool DuSpectateur, bool Crt)? FilmEnCours()
    {
        var c = _enCours;
        return c is null ? null : (c.Fichier, c.Rotation, c.Forme, c.Fond, c.DuSpectateur, c.Crt);
    }

    /// <summary>
    /// GARDER LE FILM D'UNE RENCONTRE (CDC films MULTIPLAYER, 08/11). L'exe du direct rouvre le film par son chemin a chaque
    /// lecture : il se range dans le dossier des captures une fois la fin de passage jouee (l'exe sorti, deux minutes au plus).
    /// Sa fiche dit la gameroom, la portion (du vrai depart moins 2 s a la fin lue plus le delai de fermeture) et l'heure de sa
    /// premiere image ; le site verifie la portion contre le vrai depart et la fin qu'il a notes. Gardee 48 h au plus.
    /// </summary>
    private async Task GarderLaRencontreAsync(CaptureEnCours c, PortionDeRencontre portion, double dureeDuFilmS)
    {
        string? carteAvant = null, carteApres = null;
        try
        {
            // LES CARTES DU FILM (D-M4, lot M4) : le jeu vient de se fermer, l'ecran de fin parait dans les secondes qui
            // suivent. Rendues tout de suite (une revanche peut suivre), avant d'attendre la fin de passage du direct.
            (carteAvant, carteApres) = await RendreLesCartesAsync(c).ConfigureAwait(false);
            var limite = DateTime.UtcNow + FinDePassageAuPlus;
            while (DirectEnCours() && DateTime.UtcNow < limite)
                await Task.Delay(TimeSpan.FromSeconds(5), _arret).ConfigureAwait(false);
            var destination = Path.Combine(_dossier, c.ReplayId + ".mkv");
            for (var essai = 1; ; essai++)
            {
                try
                {
                    Effacer(destination);
                    File.Move(c.Fichier, destination);
                    break;
                }
                catch (IOException) when (essai < 12)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _arret).ConfigureAwait(false);
                }
            }
            var entete = MatroskaEntete.Lire(destination);
            var origine = entete is { Origine: true };
            if (entete is null || (entete.Couchee && !(origine && c.Rotation is 1 or 3)))
            {
                Effacer(destination);
                EffacerLesCartes(carteAvant, carteApres);
                _logger.LogInformation("Capture : rencontre {Room} non gardee ({Raison}).", c.Room ?? c.ReplayId,
                    entete is null ? "film illisible" : "jeu vertical, film couche");
                return;
            }
            var gardee = new CaptureGardee(c.ReplayId, destination, 0, 0, DateTime.UtcNow, SansReplay: true, Origine: origine,
                Rotation: c.Rotation, DureeS: dureeDuFilmS, DebutSequenceS: portion.DebutS, FinSequenceS: portion.FinS,
                // Le spectateur montre deux ou quatre consoles cote a cote : la forme d'une seule ne vaut pas pour son image.
                Forme: c.DuSpectateur ? null : c.Forme, Largeur: (int)entete.Largeur, Hauteur: (int)entete.Hauteur, Fond: c.Fond,
                Crt: c.Crt, Room: c.Room, DebutUtc: c.DebutUtc, VraiDepartS: SecondesDuFilm(c.DebutUtc, c.VraiDepartUtc),
                FinLueS: SecondesDuFilm(c.DebutUtc, c.FinLueUtc), RencontreComplete: portion.Complete, Coop: c.Coop, Issue: c.Issue,
                DuSpectateur: c.DuSpectateur,
                CarteAvant: carteAvant, CarteAvantS: carteAvant is null ? 0 : RetroBat.Api.Multiplayer.MultiplayerCartesDuFilm.DureeAvantS,
                CarteApres: carteApres, CarteApresS: carteApres is null ? 0 : RetroBat.Api.Multiplayer.MultiplayerCartesDuFilm.DureeApresS);
            await SauverAsync(gardee, Path.Combine(_dossier, c.ReplayId + ".json"), _arret).ConfigureAwait(false);
            _logger.LogInformation(
                "Capture : rencontre {Room} gardee, portion {Debut} s a {Fin} s du film ({Duree} s, {Complete}{Spectateur}), cartes {Cartes}, en attente de la decision du site.",
                c.Room ?? "?", Texte(portion.DebutS), Texte(portion.FinS), Texte(portion.DureeS),
                portion.Complete ? "rencontre complete" : "sans fin lue", c.DuSpectateur ? ", film du spectateur" : "",
                TexteDesCartes(carteAvant, carteApres));
            _reveil.Release();
        }
        catch (OperationCanceledException) { /* arret de l'API : le film reste ou il est */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : rencontre {Room} non gardee.", c.Room ?? c.ReplayId);
            Effacer(c.Fichier);
            EffacerLesCartes(carteAvant, carteApres);
        }
    }

    /// <summary>« VS 3 s et fin 6 s », « VS 3 s, sans ecran de fin », « aucune ». Pur.</summary>
    internal static string TexteDesCartes(string? avant, string? apres)
        => (avant, apres) switch
        {
            (not null, not null) => "VS 3 s et fin 6 s",
            (not null, null) => "VS 3 s, sans ecran de fin",
            (null, not null) => "fin 6 s, sans VS",
            _ => "aucune",
        };

    /// <summary>
    /// Les cartes du film d'une rencontre, a cote de sa fiche : seulement avec un exe qui les pose (0.7.2), sinon la version
    /// YouTube durerait 9 s de moins que ce que la borne annonce.
    /// </summary>
    private async Task<(string? Avant, string? Apres)> RendreLesCartesAsync(CaptureEnCours c)
    {
        if (_cartes is null || c.Room is not { Length: > 0 } room || !LanceurDuDirect.SaitLesCartes(LanceurDuDirect.Chemin(AppContext.BaseDirectory)))
            return (null, null);
        try
        {
            Directory.CreateDirectory(_dossier);
            return await _cartes.RendreLesCartesDeLaVersionAsync(room, Path.Combine(_dossier, c.ReplayId + ".vs.png"),
                Path.Combine(_dossier, c.ReplayId + ".fin.png"), RetroBat.Api.Multiplayer.MultiplayerCartesDuFilm.AttenteDeLaFin, _arret)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Capture : cartes de la rencontre {Room} non rendues ({Message}).", room, ex.Message);
            return (null, null);
        }
    }

    private static void EffacerLesCartes(string? avant, string? apres)
    {
        if (avant is not null) Effacer(avant);
        if (apres is not null) Effacer(apres);
    }

    /// <summary>
    /// Les cartes d'une capture gardee, telles qu'elles partiront : une carte dont l'image manque, ou un exe qui ne les pose
    /// pas, compte 0 s (le site l'apprend avant l'envoi). Une version deja fabriquee les porte deja : rien ne change. Pur.
    /// </summary>
    internal static CaptureGardee CartesQuiPartent(CaptureGardee g, bool exeSaitLesCartes, Func<string, bool> existe)
    {
        if (g.Version is not null) return g;
        var avant = exeSaitLesCartes && g.CarteAvant is { } a && g.CarteAvantS > 0 && existe(a);
        var apres = exeSaitLesCartes && g.CarteApres is { } b && g.CarteApresS > 0 && existe(b);
        return g with
        {
            CarteAvant = avant ? g.CarteAvant : null, CarteAvantS = avant ? g.CarteAvantS : 0,
            CarteApres = apres ? g.CarteApres : null, CarteApresS = apres ? g.CarteApresS : 0,
        };
    }

    private static string Texte(double secondes) => secondes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>L'exe du direct joue sa fin de passage deux minutes au plus apres la fin de la partie.</summary>
    private static readonly TimeSpan FinDePassageAuPlus = TimeSpan.FromMinutes(2);

    /// <summary>L'exe du direct tourne-t-il (direct, fin de passage, ou version YouTube d'un film) ?</summary>
    private static bool DirectEnCours()
    {
        try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LanceurDuDirect.Exe)).Length > 0; }
        catch (Exception) { return false; }
    }

    private static DateTime? DerniereEcritureUtc(string fichier)
    {
        try { return File.Exists(fichier) ? File.GetLastWriteTimeUtc(fichier) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// LE FOND DU CADRE 16:9 (28 octobre 2026) : le fanart du jeu dans le gamelist d'ES, sinon sa capture ; null sans image.
    /// L'exe du direct le pose couvrant, flou et assombri derriere le jeu.
    /// </summary>
    internal static string? FondDuJeu(string? cheminDuJeu)
        => string.IsNullOrWhiteSpace(cheminDuJeu) ? null : RetroBat.Api.Media.MediaDuGamelist.Resoudre(cheminDuJeu, "fanart", "image");

    /// <summary>
    /// Le verdict d'un score : sa session est celle du film sans replay en cours, ou de celui qui vient de finir (dix
    /// minutes au plus, tant qu'aucune autre partie n'est filmee). Le film d'une partie avec replay n'en a pas besoin.
    /// </summary>
    private void NoterSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session)) return;
        lock (_verrouSessions)
        {
            string? film = null;
            if (_enCours is { SansReplay: true } c) film = c.ReplayId;
            else if (_enCours is null && _dernierSansReplay is { } d && DateTime.UtcNow - d.Fin < SessionsTardivesPendant) film = d.Id;
            if (film is null) return;
            if (!_sessionsDesFilms.TryGetValue(film, out var sessions))
                _sessionsDesFilms[film] = sessions = new HashSet<string>(StringComparer.Ordinal);
            sessions.Add(session.Trim());
        }
    }

    /// <summary>Les sessions connues d'un film : celles deja gardees, plus celles notees depuis.</summary>
    private IReadOnlyList<string> SessionsDuFilm(string film, IReadOnlyList<string>? deja)
    {
        lock (_verrouSessions)
        {
            var toutes = new SortedSet<string>(deja ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (_sessionsDesFilms.TryGetValue(film, out var notees)) toutes.UnionWith(notees);
            return toutes.ToList();
        }
    }

    private void OublierLesSessions(string film)
    {
        lock (_verrouSessions) _sessionsDesFilms.Remove(film);
    }

    /// <summary>
    /// La cadence d'une partie sans replay se lit aux trames emulees que les signaux du jeu portent (plugin Lua de
    /// MAME). Un seul emetteur : deux compteurs differents se meleraient.
    /// </summary>
    private void SuivreLaCadence(CaptureEnCours c, string source, long frame)
    {
        lock (_verrouSessions)
        {
            if (c.Lent || !ReferenceEquals(_enCours, c)) return;
            c.SourceDesTrames ??= source;
            if (!string.Equals(c.SourceDesTrames, source, StringComparison.Ordinal)) return;
            if (!c.Garde.Relever(c.Chrono.Elapsed.TotalSeconds, frame)) return;
            c.Lent = true;
        }
        MarquerModeste();
        _ = Task.Run(() => ArreterAsync(c.ReplayId, null, abandon: true,
            "le jeu ralentissait (reference " + c.Garde.Reference.ToString("F1") + " images/s)"));
    }

    /// <summary>L'emetteur et la trame emulee d'un signal du jeu (<c>ingame.memory.changed</c>), ou null. Pur.</summary>
    internal static (string Source, long Frame)? TrameDuJeu(object? payload)
    {
        try
        {
            var el = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("Source", out var source) || source.ValueKind != JsonValueKind.String
                || !el.TryGetProperty("signal", out var signal) || signal.ValueKind != JsonValueKind.Object
                || !signal.TryGetProperty("Frame", out var f) || f.ValueKind != JsonValueKind.Number
                || !f.TryGetInt64(out var frame) || frame <= 0)
                return null;
            return (source.GetString() ?? "", frame);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Pendant la capture : l'en-tete (jeu vertical ?) puis la cadence du jeu.</summary>
    private async Task SurveillerAsync(CancellationToken ct)
    {
        var c = _enCours;
        if (c is null) return;

        if (!c.EnteteVerifiee && MatroskaEntete.Lire(c.Fichier) is { } entete)
        {
            c.EnteteVerifiee = true;
            // Un film couche se redresse a la fin (ou dans sa version YouTube), si l'on sait dans quel sens. Un film a la
            // taille d'origine est petit par nature.
            var redressable = entete.Couchee && c.Rotation is 1 or 3;
            if ((entete.Couchee && !redressable) || (!entete.Origine && entete.Largeur < LargeurMinimale))
            {
                await ArreterAsync(c.ReplayId, null, abandon: true,
                    entete.Couchee ? "jeu vertical, sens de rotation inconnu" : "image de " + entete.Largeur + " pixels, echelle x4 absente").ConfigureAwait(false);
                return;
            }
        }

        // Le film depasse le plafond d'envoi sans s'arreter : il ne partira pas sur YouTube, mais le direct d'un record
        // tardif en est la copie. Seuls le plafond local et l'espace libre du disque l'arretent.
        if (TailleDe(c.Fichier) > _tailleLocaleMax)
        {
            await ArreterAsync(c.ReplayId, null, abandon: true, "film plus gros que " + _tailleLocaleMax / 1_000_000 + " Mo").ConfigureAwait(false);
            return;
        }
        if (EspaceLibreGo(Path.GetDirectoryName(c.Fichier) ?? _dossier) is { } libre && libre < _espaceLibreMinGo)
        {
            await ArreterAsync(c.ReplayId, null, abandon: true, "plus que " + libre.ToString("F1") + " Go libres").ConfigureAwait(false);
            return;
        }

        var actif = await _ra.GetActiveReplayAsync(ct).ConfigureAwait(false);
        if (actif is not { Recording: true }) return;
        if (c.Garde.Relever(c.Chrono.Elapsed.TotalSeconds, actif.Frame))
        {
            MarquerModeste();
            await ArreterAsync(c.ReplayId, null, abandon: true,
                "le jeu ralentissait (reference " + c.Garde.Reference.ToString("F1") + " images/s)").ConfigureAwait(false);
        }
    }

    /// <summary>Hors de toute partie : chaque capture gardee est envoyee si c'est un record, effacee sinon.</summary>
    private async Task EnvoyerLesCapturesAsync(CancellationToken stoppingToken)
    {
        if (_enCours is not null || EmulatorForeground.EmulateurTourne()) return;
        // Les records passent avant les rencontres (D-M8, 08/11) : leur part du plafond d'envois reste la leur.
        var fiches = FichesDesFilms(_dossier).OrderBy(f => EstUnFilmMultiplayer(Path.GetFileNameWithoutExtension(f))).ToList();
        foreach (var fiche in fiches)
        {
            if (_enCours is not null || EmulatorForeground.EmulateurTourne()) return;
            CaptureGardee? g = null;
            try { g = JsonSerializer.Deserialize<CaptureGardee>(await File.ReadAllTextAsync(fiche, stoppingToken).ConfigureAwait(false)); }
            catch (Exception) { /* fiche abimee */ }
            if (g is null || !File.Exists(g.Fichier))
            {
                Effacer(fiche);
                continue;
            }
            if (EstUnFilmMultiplayer(g.ReplayId))
            {
                if (!await EnvoyerLaRencontreAsync(g, fiche, stoppingToken).ConfigureAwait(false)) return;
                continue;
            }

            var manifest = _manifests.GetManifest(g.ReplayId);
            var fps = manifest?.Frames.NominalFps is > 1 ? manifest.Frames.NominalFps : 60;
            var fin = Math.Max(g.FinFilm, manifest?.Frames.ReplayEnd ?? 0);

            // Deja envoyee : le film reste sur la borne jusqu'a ce que la video soit prete. Refusee par YouTube,
            // ou perdue, elle repart ; confirmation manquee, elle est refaite.
            if (g.VideoId is not null)
            {
                var etat = await _publication.EtatDuFilmAsync(g.ReplayId, stoppingToken).ConfigureAwait(false);
                if (etat == "ready")
                {
                    Oublier(g, fiche, "prete sur YouTube (" + g.VideoId + ")");
                    continue;
                }
                if (etat is "failed" or "none")
                {
                    _logger.LogInformation("Capture : {ReplayId} refusee ou perdue par YouTube, renvoyee.", g.ReplayId);
                    g = g with { VideoId = null, Session = null, EnvoyeeUtc = null };
                    await SauverAsync(g, fiche, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    if (etat == "uploading")
                    {
                        var (debutPortion, finPortion) = PortionDuReplay(g, fin, fps);
                        await _publication.ConfirmerAsync(g.ReplayId, g.VideoId, debutPortion, finPortion, fps, stoppingToken).ConfigureAwait(false);
                    }
                    if (DateTime.UtcNow - (g.EnvoyeeUtc ?? g.CreeeUtc) > PreteAuPlus)
                        Oublier(g, fiche, "envoyee il y a plus de 24 heures (" + g.VideoId + ")");
                    continue;
                }
            }

            var age = DateTime.UtcNow - g.CreeeUtc;
            // Trop tot : le site ne connait pas encore la partie.
            if (age < PremiereDemandeApres) continue;
            if (age > _ageMax)
            {
                Oublier(g, fiche, "trop ancienne");
                continue;
            }
            if (TailleDe(g.Version ?? g.Fichier) > _tailleMax)
            {
                Oublier(g, fiche, "plus grosse que " + _tailleMax / 1_000_000 + " Mo");
                continue;
            }
            // Sans replay : les sessions des scores arrives depuis la fin du film le rejoignent.
            if (g.SansReplay && SessionsDuFilm(g.ReplayId, g.Sessions) is { } sessions && sessions.Count != (g.Sessions?.Count ?? 0))
            {
                g = g with { Sessions = sessions };
                await SauverAsync(g, fiche, stoppingToken).ConfigureAwait(false);
            }

            using var envoi = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_verrou) _envoiEnCours = envoi;
            try
            {
                var courante = g;
                // Film a la taille d'origine : sa version YouTube se fabrique d'abord, et seulement pour un record.
                if (courante.Origine && (courante.Version is null || !File.Exists(courante.Version)))
                {
                    var voulue = await _publication.CaptureVoulueAsync(courante.ReplayId, TailleDe(courante.Fichier),
                        courante.Sessions, envoi.Token).ConfigureAwait(false);
                    if (voulue.Issue != ReplayVideoPublisher.IssueDeCapture.Voulue)
                    {
                        SuivreLeRefus(courante, fiche, voulue, age);
                        continue;
                    }
                    // Seule la sequence du record part (decision du 15 octobre 2026) : elle finit avec le run que le site
                    // designe (frames du replay), sinon avec celui que la borne a vu finir, sinon avec le film.
                    var finRunS = voulue.RunFinFrame is long finRun && finRun > courante.DebutFilm && fps > 1
                        ? (finRun - courante.DebutFilm) / fps
                        : courante.FinDuRunS ?? courante.DureeS;
                    if (voulue.Epreuve)
                    {
                        // Une epreuve (08/11) : du depart a l'arrivee que le site designe, jamais la coupe au score.
                        if (SequenceDEpreuve(courante.DebutFilm, voulue.RunDebutFrame, voulue.RunFinFrame, fps, courante.DureeS) is { } epreuve)
                            courante = courante with { DebutSequenceS = epreuve.DebutS, FinSequenceS = epreuve.FinS };
                    }
                    else if (finRunS is double finDuRunS)
                    {
                        var (debutS, finS) = SequenceDuRecord(courante.Courbe, finDuRunS);
                        courante = courante with { DebutSequenceS = debutS, FinSequenceS = finS };
                    }
                    // Moins de 20 s (07/11, 20 s depuis le 08/11) : ni sur le site ni sur YouTube ; la capture s'oublie, rien ne
                    // part. Sauf une epreuve, coupee du depart a l'arrivee de son run : elle part quelle que soit sa duree.
                    var dureeDuFilmS = courante.DebutSequenceS is double ds && courante.FinSequenceS is double fs ? fs - ds : courante.DureeS;
                    if (!voulue.Epreuve && dureeDuFilmS is double duree && duree < ReplayVideoPublisher.DureeMinimaleS)
                    {
                        Oublier(courante, fiche, "moins de " + ReplayVideoPublisher.DureeMinimaleS + " s (" + duree.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s)");
                        continue;
                    }
                    // Jamais un film deforme sur YouTube (25/10) : la video du record se fera depuis son replay.
                    if (FormeRefusee(courante.Largeur, courante.Hauteur, courante.Rotation, courante.Forme,
                            ExeSaitLaForme(LanceurDuDirect.Chemin(AppContext.BaseDirectory))) is { } refus)
                    {
                        Oublier(courante, fiche, "forme de l'image : " + refus);
                        continue;
                    }
                    courante = await FabriquerLaVersionAsync(courante, fiche, envoi.Token).ConfigureAwait(false);
                    if (courante.Version is null) continue;
                    if (TailleDe(courante.Version) > _tailleMax)
                    {
                        Oublier(courante, fiche, "version YouTube plus grosse que " + _tailleMax / 1_000_000 + " Mo");
                        continue;
                    }
                }
                var (debutPortion, finPortion) = PortionDuReplay(courante, fin, fps);
                var resultat = await _publication.EnvoyerCaptureAsync(courante.ReplayId, courante.Version ?? courante.Fichier,
                    debutPortion, finPortion, fps, courante.Sessions, courante.Session, async session =>
                    {
                        courante = courante with { Session = session };
                        await SauverAsync(courante, fiche, stoppingToken).ConfigureAwait(false);
                    }, envoi.Token).ConfigureAwait(false);
                if (resultat.Issue == ReplayVideoPublisher.IssueDeCapture.Envoyee)
                {
                    await SauverAsync(courante with { Session = null, VideoId = resultat.VideoId, EnvoyeeUtc = DateTime.UtcNow },
                        fiche, stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Capture : {ReplayId} envoyee ({Detail}), film garde jusqu'a la video prete.",
                        courante.ReplayId, resultat.Detail);
                }
                else SuivreLeRefus(courante, fiche, resultat, age);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Capture : envoi de {ReplayId} interrompu, une partie commence.", g.ReplayId);
                return;
            }
            finally
            {
                lock (_verrou) _envoiEnCours = null;
            }
        }
    }

    /// <summary>
    /// LE FILM D'UNE RENCONTRE PART (CDC films MULTIPLAYER, lot M3, 08/11). Hors partie, la borne hote demande au site s'il le
    /// veut (<c>source = multiplayer</c>, la gameroom, la portion et ses heures) ; oui, l'exe du direct fabrique la version
    /// YouTube de la portion, puis la borne l'envoie chez Google et le confirme. Un jeu qui demarre interrompt l'envoi, qui
    /// reprend apres (la session d'envoi est gardee). Rend faux quand une partie a commence.
    /// </summary>
    private async Task<bool> EnvoyerLaRencontreAsync(CaptureGardee g, string fiche, CancellationToken stoppingToken)
    {
        var nom = "rencontre " + (g.Room ?? g.ReplayId);
        if (g.VideoId is not null)
        {
            var etat = await _publication.EtatDuFilmAsync(g.ReplayId, stoppingToken).ConfigureAwait(false);
            if (etat == "ready")
            {
                Oublier(g, fiche, "prete sur YouTube (" + g.VideoId + ")");
                return true;
            }
            if (etat is "failed" or "none")
            {
                _logger.LogInformation("Capture : {Nom} refusee ou perdue par YouTube, renvoyee.", nom);
                await SauverAsync(g with { VideoId = null, Session = null, EnvoyeeUtc = null }, fiche, stoppingToken).ConfigureAwait(false);
                return true;
            }
            if (etat == "uploading")
                await _publication.ConfirmerAsync(g.ReplayId, g.VideoId, 0, 0, 0, stoppingToken).ConfigureAwait(false);
            if (DateTime.UtcNow - (g.EnvoyeeUtc ?? g.CreeeUtc) > PreteAuPlus)
                Oublier(g, fiche, "envoyee il y a plus de 24 heures (" + g.VideoId + ")");
            return true;
        }

        var age = DateTime.UtcNow - g.CreeeUtc;
        // Trop tot : la plateforme n'a pas encore la fin de la gameroom (l'hote l'envoie apres la fermeture du jeu).
        if (age < PremiereDemandeApres) return true;
        if (age > _ageMax)
        {
            Oublier(g, fiche, "trop ancienne");
            return true;
        }

        using var envoi = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_verrou) _envoiEnCours = envoi;
        try
        {
            var courante = g;
            if (courante.Version is null || !File.Exists(courante.Version))
            {
                // Les cartes qui partiront vraiment : le site compte leurs secondes dans la duree du film.
                courante = CartesQuiPartent(courante with { Version = null },
                    LanceurDuDirect.SaitLesCartes(LanceurDuDirect.Chemin(AppContext.BaseDirectory)), File.Exists);
                var voulue = await _publication.RencontreVoulueAsync(courante.ReplayId, TailleEstimee(courante, _tailleMax),
                    ChampsDeLaRencontre(courante, DateTime.UtcNow), envoi.Token).ConfigureAwait(false);
                if (voulue.Issue != ReplayVideoPublisher.IssueDeCapture.Voulue)
                {
                    SuivreLeRefusDeLaRencontre(courante, fiche, voulue, age);
                    return true;
                }
                // Jamais un film deforme sur YouTube (25/10). Le film du spectateur montre plusieurs consoles a pixels carres :
                // sa forme est celle de son image.
                if (!courante.DuSpectateur && FormeRefusee(courante.Largeur, courante.Hauteur, courante.Rotation, courante.Forme,
                        ExeSaitLaForme(LanceurDuDirect.Chemin(AppContext.BaseDirectory))) is { } refus)
                {
                    Oublier(courante, fiche, "forme de l'image : " + refus);
                    return true;
                }
                courante = await FabriquerLaVersionAsync(courante, fiche, envoi.Token).ConfigureAwait(false);
                if (courante.Version is null) return true;
                if (TailleDe(courante.Version) > _tailleMax)
                {
                    Oublier(courante, fiche, "version YouTube plus grosse que " + _tailleMax / 1_000_000 + " Mo");
                    return true;
                }
            }
            var resultat = await _publication.EnvoyerCaptureAsync(courante.ReplayId, courante.Version!, 0, 0, 0, null, courante.Session,
                async session =>
                {
                    courante = courante with { Session = session };
                    await SauverAsync(courante, fiche, stoppingToken).ConfigureAwait(false);
                }, envoi.Token, ChampsDeLaRencontre(courante, DateTime.UtcNow)).ConfigureAwait(false);
            if (resultat.Issue == ReplayVideoPublisher.IssueDeCapture.Envoyee)
            {
                await SauverAsync(courante with { Session = null, VideoId = resultat.VideoId, EnvoyeeUtc = DateTime.UtcNow },
                    fiche, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Capture : {Nom} envoyee ({Detail}, {Taille} octets), film garde jusqu'a la video prete.",
                    nom, resultat.Detail, TailleDe(courante.Version!));
            }
            else SuivreLeRefusDeLaRencontre(courante, fiche, resultat, age);
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Capture : envoi de la {Nom} interrompu, une partie commence.", nom);
            return false;
        }
        finally
        {
            lock (_verrou) _envoiEnCours = null;
        }
    }

    /// <summary>
    /// Ce que le site n'accepte pas d'une rencontre : un refus definitif (pas l'hote, sans vrai depart, partie libre, portion
    /// decalee, trop courte ou trop longue, deja filmee) efface le film ; une gameroom toujours pas finie au site apres trois
    /// heures aussi ; le reste (quota, reseau) attend le prochain passage.
    /// </summary>
    private void SuivreLeRefusDeLaRencontre(CaptureGardee g, string fiche, ReplayVideoPublisher.ResultatDeCapture r, TimeSpan age)
    {
        var nom = "rencontre " + (g.Room ?? g.ReplayId);
        if (r.Issue == ReplayVideoPublisher.IssueDeCapture.Inutile) Oublier(g, fiche, "refusee par le site (" + r.Detail + ")");
        else if (r.Detail == "pas_encore" && age > PasEncoreAuPlus) Oublier(g, fiche, "gameroom toujours pas finie au site");
        else _logger.LogInformation("Capture : {Nom} attend ({Detail}).", nom, r.Detail);
    }

    /// <summary>
    /// La taille annoncee au site avant la fabrication de la version YouTube : environ 1 Mo par seconde de portion (le cadre
    /// 1280 x 720, a mesurer au lot M3), jamais plus que le plafond d'envoi ; la vraie taille se verifie apres. Pur.
    /// </summary>
    internal static long TailleEstimee(CaptureGardee g, long plafond)
    {
        var duree = g.DebutSequenceS is double d && g.FinSequenceS is double f && f > d ? f - d : g.DureeS ?? 60;
        return Math.Max(1, Math.Min(plafond, (long)Math.Round((duree + g.CarteAvantS + g.CarteApresS) * 1_000_000)));
    }

    /// <summary>
    /// Ce que la demande d'envoi d'une rencontre dit au site : la gameroom, la portion en secondes du film et en heures UTC de
    /// la borne, l'heure de la borne a l'envoi (le site corrige ainsi son horloge), la rencontre complete ou non, et les
    /// cartes d'avant et d'apres (ecran de lancement VS et ecran de fin, lot M4 ; 0 tant que l'exe ne les pose pas). Pur.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject ChampsDeLaRencontre(CaptureGardee g, DateTime maintenant)
    {
        static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var champs = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = "multiplayer",
            ["room"] = g.Room,
            ["start_s"] = g.DebutSequenceS,
            ["end_s"] = g.FinSequenceS,
            ["film_s"] = g.DureeS,
            ["complete"] = g.RencontreComplete,
            ["coop"] = g.Coop,
            ["spectator"] = g.DuSpectateur,
            ["intro_s"] = g.CarteAvantS,
            ["outro_s"] = g.CarteApresS,
            ["device_now_utc"] = Iso(maintenant),
        };
        if (g.DebutUtc is DateTime debut && g.DebutSequenceS is double ds && g.FinSequenceS is double fs)
        {
            champs["portion_start_utc"] = Iso(debut.AddMilliseconds(Math.Round(ds * 1000)));
            champs["portion_end_utc"] = Iso(debut.AddMilliseconds(Math.Round(fs * 1000)));
        }
        return champs;
    }

    /// <summary>
    /// Ce que le site n'accepte pas encore : un refus definitif efface le film ; une partie toujours pas reliee a un
    /// score classe apres trois heures aussi ; le reste attend le prochain passage.
    /// </summary>
    private void SuivreLeRefus(CaptureGardee g, string fiche, ReplayVideoPublisher.ResultatDeCapture r, TimeSpan age)
    {
        if (r.Issue == ReplayVideoPublisher.IssueDeCapture.Inutile) Oublier(g, fiche, r.Detail);
        else if (r.Detail is "pas_encore" or "replay.not_owned" && age > PasEncoreAuPlus)
            Oublier(g, fiche, "toujours pas reliee a un score classe");
        else _logger.LogInformation("Capture : {ReplayId} attend ({Detail}).", g.ReplayId, r.Detail);
    }

    /// <summary>
    /// La version YouTube d'un film a la taille d'origine (NelfePlay.LiveStreaming.exe), hors partie : redressee,
    /// agrandie sans lissage, en FLV a cote du film. Une partie qui commence l'arrete (elle reprendra) ; apres trois
    /// echecs, le film n'est plus garde. Rend la capture, avec sa version si elle est prete.
    /// </summary>
    private async Task<CaptureGardee> FabriquerLaVersionAsync(CaptureGardee g, string fiche, CancellationToken ct)
    {
        var exe = LanceurDuDirect.Chemin(AppContext.BaseDirectory);
        if (exe is null)
        {
            _logger.LogWarning("Capture : {ReplayId} attend sa version YouTube, {Exe} absent.", g.ReplayId, LanceurDuDirect.Exe);
            return g;
        }
        var version = Path.ChangeExtension(g.Fichier, ".flv");
        Effacer(version);
        _logger.LogInformation("Capture : {ReplayId} est {Quoi}, fabrication de sa version YouTube ({Sequence}).", g.ReplayId,
            EstUnFilmMultiplayer(g.ReplayId) ? "une rencontre voulue par le site" : "un record",
            g.DebutSequenceS is double d && g.FinSequenceS is double f ? "sequence de " + d + " a " + f + " s du film" : "film entier");
        var r = await LanceurDuDirect.FabriquerLaVersionAsync(exe, g.Fichier, version, g.Rotation ?? 0, _logger, ct,
            g.DebutSequenceS, g.FinSequenceS, g.Forme, g.Fond, g.Crt, g.CarteAvant, g.CarteAvantS, g.CarteApres, g.CarteApresS)
            .ConfigureAwait(false);
        if (r.Code == 0 && TailleDe(version) > 0)
        {
            var faite = g with { Version = version };
            await SauverAsync(faite, fiche, _arret).ConfigureAwait(false);
            _logger.LogInformation("Capture : version YouTube de {ReplayId} prete ({Taille} octets en {Duree:F0} s).",
                g.ReplayId, TailleDe(version), r.Duree.TotalSeconds);
            return faite;
        }
        Effacer(version);
        var ratee = g with { EchecsDeVersion = g.EchecsDeVersion + 1 };
        if (ratee.EchecsDeVersion >= EchecsDeVersionMax)
        {
            Oublier(ratee, fiche, "version YouTube impossible (" + (r.DerniereLigne ?? "code " + r.Code) + ")");
            return ratee;
        }
        await SauverAsync(ratee, fiche, _arret).ConfigureAwait(false);
        _logger.LogWarning("Capture : version YouTube de {ReplayId} ratee (code {Code} : {Ligne}), nouvel essai au prochain passage.",
            g.ReplayId, r.Code, r.DerniereLigne);
        return ratee;
    }

    private static async Task SauverAsync(CaptureGardee g, string fiche, CancellationToken ct)
    {
        var temporaire = fiche + ".tmp";
        await File.WriteAllTextAsync(temporaire, JsonSerializer.Serialize(g, Json), Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temporaire, fiche, overwrite: true);
    }

    private void Oublier(CaptureGardee g, string fiche, string pourquoi)
    {
        Effacer(g.Fichier);
        if (g.Version is not null) Effacer(g.Version);
        EffacerLesCartes(g.CarteAvant, g.CarteApres);
        Effacer(fiche);

        OublierLesSessions(g.ReplayId);
        _logger.LogInformation("Capture : {ReplayId} {Pourquoi}, film efface de la borne.", g.ReplayId, pourquoi);
    }

    /// <summary>
    /// Les reglages des films de RetroArch, poses au demarrage, hors partie, comme les autres reglages qu'APIExpose
    /// impose a RetroArch (et avant chaque lancement par <see cref="EchelleDesFilms"/>). Taille d'origine : le fichier de
    /// reglages d'APIExpose. Film agrandi : l'echelle x4 au moins (une image de jeu de 256 pixels de large n'aurait sur
    /// YouTube que du 240p flou).
    /// </summary>
    private void PreparerRetroArch()
    {
        try
        {
            var chemin = RetroBatPaths.RetroArchConfigPath;
            if (!File.Exists(chemin) || Process.GetProcessesByName("retroarch").Length > 0) return;
            if (_origine) EcrireLesReglagesDuFilm();
            var contenu = File.ReadAllText(chemin);
            var nouveau = ReglagesDesFilms(contenu, _origine, CheminDesReglagesDuFilm);
            if (!_origine)
            {
                // Le facteur de chaque jeu se pose avant son lancement (EchelleDesFilms) : au demarrage, seul un facteur
                // absent ou de 1 (celui d'origine de RetroArch) devient x4.
                var courant = nouveau ?? contenu;
                if (!(int.TryParse(ValeurDans(courant, "video_record_scale_factor"), out var actuel) && actuel >= 2))
                    nouveau = AvecReglage(courant, "video_record_scale_factor", EchelleVoulue) ?? nouveau;
            }
            if (nouveau is null) return;
            File.WriteAllText(chemin, nouveau, new UTF8Encoding(false));
            _logger.LogInformation("Capture : RetroArch filmera {Format}.",
                _origine ? "a la taille d'origine (" + CheminDesReglagesDuFilm + ")" : "agrandi x" + ValeurDans(nouveau, "video_record_scale_factor"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture : reglages des films non poses.");
        }
    }

    /// <summary>Le fichier de reglages du film a la taille d'origine, reecrit s'il manque ou differe.</summary>
    internal static void EcrireLesReglagesDuFilm()
    {
        var cfg = CheminDesReglagesDuFilm;
        if (File.Exists(cfg) && File.ReadAllText(cfg) == ReglagesDuFilm) return;
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, ReglagesDuFilm, new UTF8Encoding(false));
    }

    /// <summary>
    /// Le contenu de retroarch.cfg avec les reglages des films, ou null s'il les a deja. Taille d'origine : qualite
    /// « personnalisee » (0) et notre fichier <paramref name="cfg"/>. Film agrandi : si nos reglages etaient poses, la
    /// qualite moyenne de RetroArch (2) revient. Pur, pour etre teste.
    /// </summary>
    internal static string? ReglagesDesFilms(string contenu, bool origine, string cfg)
    {
        if (origine)
        {
            var avecQualite = AvecReglage(contenu, "video_record_quality", "0");
            var avecFichier = AvecReglage(avecQualite ?? contenu, "video_record_config", cfg);
            return avecFichier ?? avecQualite;
        }
        if (ValeurDans(contenu, "video_record_quality") != "0"
            || !string.Equals(ValeurDans(contenu, "video_record_config"), cfg, StringComparison.OrdinalIgnoreCase))
            return null;
        return AvecReglage(AvecReglage(contenu, "video_record_config", "") ?? contenu, "video_record_quality", "2");
    }

    /// <summary>Le contenu de retroarch.cfg avec <c>cle = "valeur"</c>, ou null s'il l'a deja. Pur, pour etre teste.</summary>
    internal static string? AvecReglage(string contenu, string cle, string valeur)
    {
        var fin = contenu.Contains("\r\n") ? "\r\n" : "\n";
        var lignes = contenu.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var voulue = cle + " = \"" + valeur + "\"";
        for (var i = 0; i < lignes.Count; i++)
        {
            var l = lignes[i].TrimStart();
            if (!l.StartsWith(cle, StringComparison.Ordinal)) continue;
            var reste = l[cle.Length..].TrimStart();
            if (!reste.StartsWith('=')) continue;
            if (lignes[i].Trim() == voulue) return null;
            lignes[i] = voulue;
            return string.Join(fin, lignes);
        }
        if (lignes.Count > 0 && lignes[^1].Length == 0) lignes.Insert(lignes.Count - 1, voulue);
        else lignes.Add(voulue);
        return string.Join(fin, lignes);
    }

    /// <summary>La valeur de <c>cle</c> dans un contenu de retroarch.cfg, ou null. Pur.</summary>
    internal static string? ValeurDans(string contenu, string cle)
    {
        foreach (var ligne in contenu.Split('\n'))
        {
            var egal = ligne.IndexOf('=');
            if (egal > 0 && ligne[..egal].Trim() == cle) return ligne[(egal + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static Dictionary<string, string> LireRetroArch()
    {
        var reglages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ligne in File.ReadLines(RetroBatPaths.RetroArchConfigPath))
        {
            var egal = ligne.IndexOf('=');
            if (egal <= 0) continue;
            reglages[ligne[..egal].Trim()] = ligne[(egal + 1)..].Trim().Trim('"');
        }
        return reglages;
    }

    private static string? Valeur(Dictionary<string, string> reglages, string cle)
        => reglages.TryGetValue(cle, out var v) ? v : null;

    private static double? EspaceLibreGo(string dossier)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dossier))!).AvailableFreeSpace / 1e9; }
        catch (Exception) { return null; }
    }

    /// <summary>Le film grossit-il encore ? (RetroArch filme toujours.)</summary>
    private async Task<bool> GrossitAsync(string fichier)
    {
        try
        {
            var avant = new FileInfo(fichier).Length;
            await Task.Delay(1200, _arret).ConfigureAwait(false);
            return new FileInfo(fichier).Length != avant;
        }
        catch (Exception) when (!_arret.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Attend que RetroArch ait fini d'ecrire le film (taille stable deux secondes, trente au plus).</summary>
    private async Task AttendreLaFinAsync(string fichier)
    {
        long precedente = -1;
        for (var i = 0; i < 30; i++)
        {
            long taille;
            try { taille = new FileInfo(fichier).Length; }
            catch (Exception) { return; }
            if (taille == precedente) return;
            precedente = taille;
            await Task.Delay(2000, _arret).ConfigureAwait(false);
        }
    }

    private const string NomDeLaCapacite = "capacite.json";

    private string CheminDeCapacite => Path.Combine(_dossier, NomDeLaCapacite);

    /// <summary>
    /// Les fiches des films gardes, des plus anciennes aux plus recentes. Sans la marque de capacite, rangee dans le
    /// meme dossier : lue comme une fiche abimee, elle etait effacee au premier tour sans jeu, et la borne qui ralentit
    /// refilmait des la partie suivante au lieu d'attendre 30 jours (essai du 01/11).
    /// </summary>
    internal static List<string> FichesDesFilms(string dossier)
        => Directory.Exists(dossier)
            ? Directory.GetFiles(dossier, "*.json")
                .Where(f => !string.Equals(Path.GetFileName(f), NomDeLaCapacite, StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetCreationTimeUtc).ToList()
            : new List<string>();

    /// <summary>La borne a ralenti en filmant : elle ne filme plus pendant 30 jours.</summary>
    private bool EstModeste()
    {
        try
        {
            if (!File.Exists(CheminDeCapacite)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(CheminDeCapacite));
            return doc.RootElement.TryGetProperty("modeste_depuis", out var d) && d.TryGetDateTime(out var depuis)
                && DateTime.UtcNow - depuis.ToUniversalTime() < ModesteDurant;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void MarquerModeste()
    {
        try
        {
            File.WriteAllText(CheminDeCapacite, JsonSerializer.Serialize(new { modeste_depuis = DateTime.UtcNow }, Json));
            _logger.LogWarning("Capture : le jeu ralentissait pendant la capture, cette borne ne filme plus pendant {Jours} jours.",
                ModesteDurant.TotalDays);
        }
        catch (Exception) { /* au pire, elle retentera */ }
    }

    private static long TailleDe(string chemin)
    {
        try { return new FileInfo(chemin).Length; }
        catch (Exception) { return 0; }
    }

    private static void Effacer(string chemin)
    {
        try { if (File.Exists(chemin)) File.Delete(chemin); }
        catch (Exception) { /* repris au prochain tour */ }
    }

    private static string? Chaine(object? payload, string nom)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Le meme salon, ou un des deux inconnu (une API qui ne dit pas la gameroom). Pur.</summary>
    internal static bool MemeSalle(string? filmee, string? annoncee)
        => string.IsNullOrEmpty(filmee) || string.IsNullOrEmpty(annoncee) || string.Equals(filmee, annoncee, StringComparison.Ordinal);

    private static DateTime? CreationUtc(string fichier)
    {
        try { return File.GetCreationTimeUtc(fichier); }
        catch (Exception) { return null; }
    }

    /// <summary>Un instant d'une charge du bus (ISO 8601), en UTC ; null s'il manque. Pur.</summary>
    internal static DateTime? Instant(object? payload, string nom)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var t)
                ? t.ToUniversalTime() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? Booleen(object? payload, string nom)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.TryGetProperty(nom, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? Entier(object? payload, string nom)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void Dispose()
    {
        _abonnement?.Dispose();
        base.Dispose();
    }
}
