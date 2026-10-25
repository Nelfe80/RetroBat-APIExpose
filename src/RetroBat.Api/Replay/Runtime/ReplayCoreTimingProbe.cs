using System.Globalization;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Runtime;

/// <summary>Cadence annoncée par le core au chargement du contenu (av_info). <paramref name="Crc32"/>
/// est le CRC du contenu journalisé juste avant : il permet de VÉRIFIER que cette cadence est bien
/// celle de la partie en cours, et pas celle du jeu précédent. <paramref name="Aspect"/> est la forme de l'image
/// annoncée par le core (25/10 : un filtre NTSC double la largeur sans changer la forme), <paramref name="Coeur"/> le
/// nom de son fichier sans « _libretro.dll » (genesis_plus_gx).</summary>
public sealed record CoreTiming(double Fps, int Width, int Height, double SampleRate, string? Crc32, double? Aspect = null,
    string? Coeur = null);

/// <summary>
/// R3.2 — d'où vient la VRAIE cadence d'un jeu. RetroArch journalise l'av_info du core au
/// chargement du contenu, une ligne par lancement :
///
///   [INFO] [Core] Geometry: 256x192, Aspect: 1.524, FPS: 59.92, Sample rate: 44100.00 Hz.
///
/// C'est le core qui parle, donc c'est exact — et c'est la seule source qui donne 50 en PAL ou
/// les cadences propres à chaque carte d'arcade. On lit la DERNIÈRE ligne du log de sortie d'ES
/// (RetroArch y écrit sa sortie standard) au moment où la partie vient de démarrer.
///
/// Limites assumées : la ligne n'existe qu'avec log_verbosity (activé par RetroBat) ; le fps est
/// journalisé à 2 décimales (59,92 pour 59,9227 → 0,005 % d'écart, sans effet sur un seek) ; et un
/// RetroArch lancé par NOUS (lecture d'un replay) n'écrit pas dans ce log — inutile, le manifeste
/// porte déjà la cadence. Quand la ligne manque, l'appelant retombe sur la cadence MESURÉE.
/// </summary>
public sealed class ReplayCoreTimingProbe
{
    // « FPS: 59.92 » dans une ligne [Core] Geometry. Point décimal : log en culture C.
    private static readonly Regex GeometryLine = new(
        @"\[Core\]\s+Geometry:\s*(?<w>\d+)x(?<h>\d+)(?:,\s*Aspect:\s*(?<a>\d+(?:\.\d+)?))?.*?FPS:\s*(?<fps>\d+(?:\.\d+)?)(?:.*?Sample rate:\s*(?<sr>\d+(?:\.\d+)?))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // « [Core] Loading dynamic libretro core from: "...\core_proxy\genesis_plus_gx_libretro.dll". »
    private static readonly Regex CoreLine = new(
        @"Loading dynamic libretro core from:\s*""?(?:[^""]*[\\/])?(?<c>[^""\\/]+?)_libretro\.dll",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // « [Content] CRC32: 0xf9394e97. » — journalisé au chargement, juste AVANT la géométrie.
    private static readonly Regex ContentCrcLine = new(
        @"\[Content\]\s+CRC32:\s*(?:0x)?(?<crc>[0-9a-fA-F]{1,8})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Au-delà, le log ne parle plus de la partie en cours (ES ne l'a pas réécrit) : on préfère
    // ne rien affirmer plutôt que d'estampiller un manifeste avec la cadence du jeu précédent.
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromMinutes(10);

    // « [Environ] SET_ROTATION: "3" (270 deg). » : quarts de tour antihoraires demandes par le coeur.
    private static readonly Regex RotationLine = new(
        "SET_ROTATION:\\s*\"(?<r>[0-3])\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // La rotation se lit au depart du replay, qui peut venir longtemps apres le lancement (ecran titre) : le
    // controle du contenu (CRC) protege du journal d'un autre jeu.
    private static readonly TimeSpan MaxRotationLogAge = TimeSpan.FromHours(12);

    private readonly ILogger<ReplayCoreTimingProbe> _logger;

    public ReplayCoreTimingProbe(ILogger<ReplayCoreTimingProbe> logger) => _logger = logger;

    /// <summary>Ancienneté du log dont la cadence a été tirée (pour le diagnostic).</summary>
    public TimeSpan? LogAge()
    {
        var fi = new FileInfo(RetroBatPaths.EsLaunchStdoutLogPath);
        return fi.Exists ? DateTime.Now - fi.LastWriteTime : null;
    }

    /// <summary>
    /// Cadence du contenu chargé le plus récemment, ou null si le log ne la donne pas.
    /// <paramref name="ignoreAge"/> lève la borne d'ancienneté : réservé au DIAGNOSTIC (l'appelant
    /// affiche alors l'âge), jamais pour estampiller un manifeste.
    /// </summary>
    /// <summary>
    /// La rotation demandee par le coeur du contenu charge (quarts de tour antihoraires : 3 pour Ms. Pac-Man), lue
    /// dans le meme journal ; null si le journal ne la donne pas, est trop ancien, ou parle d'un autre contenu que
    /// <paramref name="crc32"/>. Sert a redresser les films des jeux verticaux.
    /// </summary>
    public int? ReadRotation(string? crc32)
    {
        try
        {
            var fi = new FileInfo(RetroBatPaths.EsLaunchStdoutLogPath);
            if (!fi.Exists || DateTime.Now - fi.LastWriteTime > MaxRotationLogAge) return null;
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lignes = new List<string>();
            string? ligne;
            while ((ligne = sr.ReadLine()) is not null) lignes.Add(ligne);
            return LireRotation(lignes, crc32);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Video : rotation du coeur illisible.");
            return null;
        }
    }

    /// <summary>La derniere rotation du journal, si son contenu est bien <paramref name="crc32"/>. Pur, pour etre teste.</summary>
    internal static int? LireRotation(IEnumerable<string> lignes, string? crc32)
    {
        int? rotation = null;
        string? crcDuJournal = null;
        foreach (var ligne in lignes)
        {
            var r = RotationLine.Match(ligne);
            if (r.Success) rotation = r.Groups["r"].Value[0] - '0';
            var c = ContentCrcLine.Match(ligne);
            if (c.Success) crcDuJournal = c.Groups["crc"].Value.ToLowerInvariant().PadLeft(8, '0');
        }
        if (!string.IsNullOrWhiteSpace(crc32) && crcDuJournal is not null
            && !string.Equals(crcDuJournal, crc32.Trim().ToLowerInvariant().PadLeft(8, '0'), StringComparison.Ordinal))
            return null;
        return rotation;
    }

    public CoreTiming? ReadLatest(bool ignoreAge = false)
    {
        try
        {
            var path = RetroBatPaths.EsLaunchStdoutLogPath;
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (!ignoreAge && DateTime.Now - fi.LastWriteTime > MaxLogAge)
            {
                _logger.LogDebug("Replay : log ES trop ancien ({Age:0}s) pour en tirer la cadence.", (DateTime.Now - fi.LastWriteTime).TotalSeconds);
                return null;
            }

            // Le log est réécrit à chaque lancement (quelques Ko) ; on prend la DERNIÈRE occurrence,
            // en retenant le CRC du contenu vu juste avant elle.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lignes = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) is not null) lignes.Add(line);
            return LireLaCadence(lignes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replay : lecture de la cadence du core impossible.");
            return null;
        }
    }

    /// <summary>
    /// La derniere geometrie du journal, avec le CRC du contenu et le core charges juste avant elle. Le log est reecrit a
    /// chaque lancement (quelques Ko) ; on prend la DERNIERE occurrence. Pur, pour etre teste.
    /// </summary>
    internal static CoreTiming? LireLaCadence(IEnumerable<string> lignes)
    {
        CoreTiming? last = null;
        string? pendingCrc = null;
        string? pendingCore = null;
        foreach (var line in lignes)
        {
            var coreMatch = CoreLine.Match(line);
            if (coreMatch.Success) { pendingCore = coreMatch.Groups["c"].Value.ToLowerInvariant(); continue; }

            var crcMatch = ContentCrcLine.Match(line);
            if (crcMatch.Success) { pendingCrc = crcMatch.Groups["crc"].Value.ToLowerInvariant().PadLeft(8, '0'); continue; }

            var m = GeometryLine.Match(line);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)) continue;
            if (fps is <= 1 or > 1000) continue; // garde-fou : une cadence aberrante ne vaut pas mieux qu'aucune
            int.TryParse(m.Groups["w"].Value, out var w);
            int.TryParse(m.Groups["h"].Value, out var h);
            double.TryParse(m.Groups["sr"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sampleRate);
            double? aspect = double.TryParse(m.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && a is > 0.2 and < 5 ? a : null;
            last = new CoreTiming(fps, w, h, sampleRate, pendingCrc, aspect, pendingCore);
        }
        return last;
    }
}
