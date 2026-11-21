using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>Ce que la collection apprend d'un fichier d'attente : l'empreinte du VRAI contenu, ou pourquoi on ne l'a pas.</summary>
public sealed record EmpreintesDuTemoin(string? Md5, string? SansEnTete, string? Raison)
{
    public bool Lu => Md5 is { Length: > 0 };
}

/// <summary>
/// LA PRESENCE SE PROUVE PAR LE CONTENU, JAMAIS PAR LE NOM (21/11, Tetris GB absent de la collection World Scoring).
///
/// Un jeu d'un paquet installe « a la volee » (<c>OnTheFlyMode</c>) n'a sur disque qu'un fichier d'attente de ~270 octets
/// (<c>APIEXPOSE_ON_THE_FLY_ROM_PLACEHOLDER_v1</c>, ligne <c>packPath=</c>) ; la vraie ROM ne sort du paquet qu'au lancement.
/// La collection mesurait ce fichier d'attente et ecrivait « ROM non reconnue ». Ici, pour un fichier d'attente seulement :
/// l'entree du paquet (et elle seule) est extraite dans un dossier provisoire, son md5 (et l'empreinte sans en-tete de la
/// console) est calcule comme pour un vrai fichier, gardes dans <c>state/nelfeplay/empreintes-des-temoins.json</c> par
/// « sha256 du paquet + entree » : relu seulement quand le paquet change. Appele pour les seuls candidats d'un jeu OUVERT
/// au score. Le lancement ne change pas : le .MEM et le wrapper voient la vraie ROM.
///
/// GARDE-FOUS (relecture de l'integrateur de la 1.9.208, 21/11) : ce code extrait des fichiers d'un paquet sur la borne
/// d'un joueur, sans qu'il l'ait demande.
/// - Jamais pendant une partie : l'appelant ne commence pas pendant une partie, et une partie qui commence PENDANT la lecture
///   l'arrete (processus tue, dossier efface) ; ce n'est pas un echec, la passe suivante relira.
/// - Le dossier provisoire est TOUJOURS efface (succes, erreur, delai depasse, partie), avec quelques reprises le temps que
///   l'extracteur tue lache ses fichiers ; un dossier laisse par une API arretee en pleine lecture est efface au premier appel.
/// - Un paquet enorme ou corrompu ne bloque pas la sync : 64 Mo au plus SORTIS du paquet (mesure sur disque pendant
///   l'extraction, la taille declaree par l'index n'est pas une garantie), 5 min au plus ; toute exception de l'extracteur
///   est un echec de cette entree, jamais une passe interrompue.
/// - Aucun chemin d'entree ne sort du dossier provisoire : entree refusee si elle a un segment « .. », une racine, un
///   « : » ou commence par « - » ou « @ » (pris pour une option par tar et 7za) ; le fichier rendu doit etre DANS le dossier.
/// - Un echec n'est pas retente en boucle : garde en memoire jusqu'au prochain demarrage ; un delai depasse ou un plafond
///   franchi (couteux et reproductible) est garde sur disque 7 jours, et un delai depasse ecarte TOUT le paquet.
/// - Pas d'empreinte perimee : la cle est le sha256 du paquet tel que l'index le donne, et l'index n'est cru que si le
///   fichier du paquet a encore la taille et la date que l'index a notees avec ce sha256 ; sans sha256, rien n'est lu.
/// </summary>
public sealed class EmpreintesDesTemoins
{
    /// <summary>Au-dela, ce n'est plus une cartouche : on n'extrait pas un disque pour une collection.</summary>
    internal const long PlafondDeLEntree = 64L * 1024 * 1024;

    /// <summary>Un echec couteux (delai, plafond) n'est retente qu'apres ce temps, ou avec une nouvelle version du paquet.</summary>
    internal static readonly TimeSpan OubliDUnEchec = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions LectureDeLIndex = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions EcritureDuCache = new() { WriteIndented = true };
    private const string Marque = "APIEXPOSE_ON_THE_FLY_ROM_PLACEHOLDER_v1";
    private const string PrefixeDuDossier = "empreinte-";
    private const string ToutLePaquet = "*";

    private readonly string _index;
    private readonly string _cache;
    private readonly Func<string, string, string, string, CancellationToken, Task<string>> _extraire;
    private readonly Func<string, string, long, (string? Md5, string? SansEnTete)> _empreintes;
    private readonly string _dossierProvisoire;
    private readonly ILogger? _journal;
    private readonly TimeSpan _delai;
    private readonly long _plafond;
    private readonly TimeSpan _pas;
    private readonly Func<DateTime> _horloge;
    private readonly object _verrou = new();
    private Cache? _memoire;
    private bool _restesEffaces;
    private readonly HashSet<string> _echecs = new(StringComparer.OrdinalIgnoreCase);
    private (long Ticks, long Taille, List<RomPackIndexEntry> Paquets)? _indexLu;

    public EmpreintesDesTemoins(ILogger? journal = null)
        : this(RetroBatPaths.RomPackInstallerIndexPath,
            Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "empreintes-des-temoins.json"),
            Path.Combine(RetroBatPaths.PluginRoot, ".temp", "package-installer"),
            RomPackInstallerService.ExtraireUneEntreeAsync,
            (chemin, systeme, plafond) => NelfePlayScoringCollectionSyncService.EmpreintesDuContenu(chemin,
                EmpreinteSansEnTete.RegleDu(systeme), plafond),
            journal)
    {
    }

    internal EmpreintesDesTemoins(string index, string cache, string dossierProvisoire,
        Func<string, string, string, string, CancellationToken, Task<string>> extraire,
        Func<string, string, long, (string? Md5, string? SansEnTete)> empreintes, ILogger? journal = null,
        TimeSpan? delai = null, long? plafond = null, TimeSpan? pas = null, Func<DateTime>? horloge = null)
    {
        _index = index;
        _cache = cache;
        _dossierProvisoire = dossierProvisoire;
        _extraire = extraire;
        _empreintes = empreintes;
        _journal = journal;
        _delai = delai ?? TimeSpan.FromMinutes(5);
        _plafond = plafond ?? PlafondDeLEntree;
        _pas = pas ?? TimeSpan.FromMilliseconds(250);
        _horloge = horloge ?? (() => DateTime.UtcNow);
    }

    /// <summary>Vrai si ce fichier est un fichier d'attente du gestionnaire de ROM (petit, portant la marque). Pur hors lecture.</summary>
    public static bool EstUnTemoin(string chemin) => Lignes(chemin) is not null;

    /// <summary>
    /// L'empreinte du vrai contenu d'un fichier d'attente ; null si ce n'en est pas un. Une seule extraction a la fois ; les
    /// empreintes lues sont gardees, un echec n'est pas retente avant le prochain demarrage (7 jours pour un delai depasse ou
    /// un plafond franchi) ou une nouvelle version du paquet. <paramref name="partieEnCours"/> est relu pendant l'extraction :
    /// une partie qui commence l'arrete.
    /// </summary>
    public EmpreintesDuTemoin? Lire(string chemin, string? systemeDuDossier, Func<bool>? partieEnCours = null)
    {
        var lignes = Lignes(chemin);
        if (lignes is null) return null;
        var systeme = lignes.GetValueOrDefault("system") is { Length: > 0 } s ? s : systemeDuDossier ?? string.Empty;
        var packPath = lignes.GetValueOrDefault("packPath") ?? string.Empty;
        var fichier = Path.GetFileName(chemin);

        var (paquet, rom) = Retrouver(systeme, packPath, fichier);
        if (paquet is null || rom is null)
            return new EmpreintesDuTemoin(null, null, "fichier d'attente sans jeu dans un paquet installé");
        if (string.IsNullOrWhiteSpace(paquet.Sha256))
            return new EmpreintesDuTemoin(null, null, "paquet sans empreinte dans l'index");

        var cle = paquet.Sha256 + "|" + rom.PackRelativePath;
        var clePaquet = paquet.Sha256 + "|" + ToutLePaquet;
        lock (_verrou)
        {
            EffacerLesRestes();
            var cache = Lire();
            if (cache.Entrees.TryGetValue(cle, out var connu) && connu.Md5 is { Length: > 0 })
                return new EmpreintesDuTemoin(connu.Md5, connu.SansEnTete, null);
            if (_echecs.Contains(cle) || _echecs.Contains(clePaquet)
                || EchecRecent(cache, cle) || EchecRecent(cache, clePaquet))
                return new EmpreintesDuTemoin(null, null, "ROM du pack illisible");

            // L'index n'est cru que pour le fichier qu'il a mesure : un paquet remplace depuis (meme chemin, autre contenu)
            // garderait sinon l'empreinte de l'ancien. Le gestionnaire le relit a son prochain controle.
            if (!PaquetInchange(paquet))
                return new EmpreintesDuTemoin(null, null, "paquet modifié depuis son installation");

            var (lu, issue) = Extraire(paquet, rom, systeme, partieEnCours);
            switch (issue)
            {
                case Issue.Lue when lu is { } empreintes:
                    cache.Entrees[cle] = new Entree
                    {
                        Md5 = empreintes.Md5, SansEnTete = empreintes.SansEnTete, LuLe = _horloge(), Paquet = paquet.PackagePath,
                    };
                    Enregistrer(cache);
                    return new EmpreintesDuTemoin(empreintes.Md5, empreintes.SansEnTete, null);
                case Issue.Interrompue:
                    // Pas un echec : la passe d'apres la partie relira.
                    return new EmpreintesDuTemoin(null, null, "lecture du pack interrompue par une partie");
                case Issue.TropLente:
                    // Un paquet trop lent pour une entree l'est pour les autres : tout le paquet est ecarte, et garde.
                    _echecs.Add(clePaquet);
                    cache.Entrees[clePaquet] = new Entree { Echec = "delai depasse", LuLe = _horloge(), Paquet = paquet.PackagePath };
                    Enregistrer(cache);
                    return new EmpreintesDuTemoin(null, null, "ROM du pack illisible (lecture trop longue)");
                case Issue.TropGrosse:
                    _echecs.Add(cle);
                    cache.Entrees[cle] = new Entree { Echec = "plafond depasse", LuLe = _horloge(), Paquet = paquet.PackagePath };
                    Enregistrer(cache);
                    return new EmpreintesDuTemoin(null, null, "ROM du pack trop grosse");
                default:
                    _echecs.Add(cle);
                    return new EmpreintesDuTemoin(null, null, "ROM du pack illisible");
            }
        }
    }

    private enum Issue
    {
        Lue,
        Echec,
        Interrompue,
        TropLente,
        TropGrosse,
    }

    private ((string? Md5, string? SansEnTete)? Lu, Issue Issue) Extraire(RomPackIndexEntry paquet, RomPackRomEntry rom, string systeme,
        Func<bool>? partieEnCours)
    {
        if (rom.Size > _plafond) return (null, Issue.TropGrosse);
        if (!File.Exists(paquet.PackagePath)) return (null, Issue.Echec);
        var entree = !string.IsNullOrWhiteSpace(rom.ArchiveEntryPath) ? rom.ArchiveEntryPath : rom.PackRelativePath;
        if (!EntreeSure(entree) || !EntreeSure(rom.PackRelativePath))
        {
            _journal?.LogWarning("World Scoring : entree {Entree} du paquet {Paquet} refusee (chemin hors du dossier provisoire)",
                entree, Path.GetFileName(paquet.PackagePath));
            return (null, Issue.Echec);
        }

        var dossier = Path.GetFullPath(Path.Combine(_dossierProvisoire, PrefixeDuDossier + Guid.NewGuid().ToString("N")));
        using var arret = new CancellationTokenSource();
        Task<string>? tache = null;
        var issue = Issue.Echec;
        try
        {
            Directory.CreateDirectory(dossier);
            if (partieEnCours?.Invoke() == true) return (null, Issue.Interrompue);
            _journal?.LogInformation("World Scoring : lecture de {Entree} dans {Paquet} pour reconnaitre un fichier d'attente",
                entree, Path.GetFileName(paquet.PackagePath));

            var chrono = Stopwatch.StartNew();
            var dernierCoupDOeil = TimeSpan.Zero;
            // Task.Run : un extracteur qui travaille avant son premier await ne bloque pas la surveillance.
            tache = Task.Run(() => _extraire(paquet.PackagePath, entree, rom.PackRelativePath, dossier, arret.Token), arret.Token);
            issue = Issue.Lue;
            while (!tache.Wait(_pas))
            {
                if (chrono.Elapsed >= _delai) { issue = Issue.TropLente; break; }
                if (TailleDu(dossier) > _plafond) { issue = Issue.TropGrosse; break; }
                // La liste des processus coute quelques millisecondes : une fois par seconde suffit.
                if (chrono.Elapsed - dernierCoupDOeil >= TimeSpan.FromSeconds(1) || dernierCoupDOeil == TimeSpan.Zero)
                {
                    dernierCoupDOeil = chrono.Elapsed;
                    if (partieEnCours?.Invoke() == true) { issue = Issue.Interrompue; break; }
                }
            }

            if (issue != Issue.Lue)
            {
                _journal?.LogWarning("World Scoring : lecture de {Entree} dans {Paquet} arretee ({Issue}, {Duree} s)",
                    entree, Path.GetFileName(paquet.PackagePath), issue, (int)chrono.Elapsed.TotalSeconds);
                return (null, issue);
            }

            var extrait = tache.GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(extrait)) return (null, Issue.Echec);
            var complet = Path.GetFullPath(extrait);
            if (!complet.StartsWith(dossier + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(complet))
                return (null, Issue.Echec);
            if (new FileInfo(complet).Length > _plafond || TailleDu(dossier) > _plafond) return (null, Issue.TropGrosse);
            var empreintes = _empreintes(complet, systeme, _plafond);
            return empreintes.Md5 is { Length: > 0 } ? (empreintes, Issue.Lue) : (null, Issue.Echec);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Toute exception de l'extracteur (archive corrompue : InvalidDataException ; 7za absent ou refuse : Win32Exception...)
            // est l'echec de cette entree. Remontee, elle interromprait TOUTE la passe de la collection, a chaque passe.
            _journal?.LogWarning(ex, "World Scoring : entree {Entree} du paquet {Paquet} non lue", entree, Path.GetFileName(paquet.PackagePath));
            return (null, issue == Issue.Lue ? Issue.Echec : issue);
        }
        finally
        {
            if (tache is { IsCompleted: false })
            {
                arret.Cancel();
                // Le processus de l'extracteur est tue par l'annulation ; on lui laisse le temps de lacher ses fichiers.
                try { tache.Wait(TimeSpan.FromSeconds(10)); }
                catch (Exception) { }
            }

            Effacer(dossier);
        }
    }

    /// <summary>Une entree d'archive qui reste sous le dossier ou on l'extrait et qu'aucun extracteur ne prend pour une option.</summary>
    internal static bool EntreeSure(string? entree)
    {
        if (string.IsNullOrWhiteSpace(entree)) return false;
        var e = entree.Trim();
        if (e.StartsWith('-') || e.StartsWith('@')) return false;
        if (e.Contains(':') || e.Contains('\0') || e.StartsWith('/') || e.StartsWith('\\') || Path.IsPathRooted(e)) return false;
        var segments = e.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Windows retire les points et espaces de fin d'un segment : « ... » ou « .. » suivi d'espaces vaut « .. ».
        return segments.Length > 0 && !segments.Any(s => s != "." && s.Trim().Trim('.').Length == 0);
    }

    private bool PaquetInchange(RomPackIndexEntry paquet)
    {
        try
        {
            var info = new FileInfo(paquet.PackagePath);
            if (!info.Exists) return false;
            // Le critere du gestionnaire lui-meme pour reprendre un paquet sans le remesurer (FindReusableInstalledPack).
            return info.Length == paquet.Size && info.LastWriteTimeUtc == paquet.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool EchecRecent(Cache cache, string cle)
        => cache.Entrees.TryGetValue(cle, out var e) && e.Echec is { Length: > 0 } && _horloge() - e.LuLe < OubliDUnEchec;

    /// <summary>La taille de ce qui est deja sorti du paquet, fichiers en cours d'ecriture compris (taille lue sur le fichier ouvert).</summary>
    private static long TailleDu(string dossier)
    {
        long total = 0;
        try
        {
            foreach (var fichier in Directory.EnumerateFiles(dossier, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var flux = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    total += flux.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    try { total += new FileInfo(fichier).Length; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    private void Effacer(string dossier)
    {
        for (var essai = 0; essai < 10; essai++)
        {
            try
            {
                if (!Directory.Exists(dossier)) return;
                Directory.Delete(dossier, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (essai == 9) _journal?.LogWarning(ex, "World Scoring : dossier provisoire {Dossier} non efface", dossier);
                else Thread.Sleep(200);
            }
        }
    }

    /// <summary>Les dossiers « empreinte-* » qu'une API arretee en pleine lecture aurait laisses : une fois par demarrage.</summary>
    private void EffacerLesRestes()
    {
        if (_restesEffaces) return;
        _restesEffaces = true;
        try
        {
            if (!Directory.Exists(_dossierProvisoire)) return;
            foreach (var reste in Directory.EnumerateDirectories(_dossierProvisoire, PrefixeDuDossier + "*"))
                Effacer(reste);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private (RomPackIndexEntry? Paquet, RomPackRomEntry? Rom) Retrouver(string systeme, string packPath, string fichier)
    {
        static string N(string v) => (v ?? string.Empty).Replace('\\', '/').Trim('/');
        var paquets = Paquets();
        foreach (var paquet in paquets.Where(p => p.OnTheFlyMode && string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var rom in paquet.Roms.Where(r => string.Equals(r.SystemId, systeme, StringComparison.OrdinalIgnoreCase)))
            {
                if (packPath.Length > 0 && string.Equals(N(rom.PackRelativePath), N(packPath), StringComparison.OrdinalIgnoreCase)) return (paquet, rom);
            }
        }

        // Sans « packPath= » exploitable : le nom du fichier laisse par le gestionnaire, dans le meme systeme.
        foreach (var paquet in paquets.Where(p => p.OnTheFlyMode && string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var rom in paquet.Roms.Where(r => string.Equals(r.SystemId, systeme, StringComparison.OrdinalIgnoreCase)))
            {
                var nom = Path.GetFileName(rom.DestinationFileName is { Length: > 0 } d ? d : rom.GamePath);
                if (packPath.Length == 0 && string.Equals(nom, fichier, StringComparison.OrdinalIgnoreCase)) return (paquet, rom);
            }
        }

        return (null, null);
    }

    private static Dictionary<string, string>? Lignes(string chemin)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists || info.Length <= 0 || info.Length > 8192) return null;
            string texte;
            using (var flux = File.Open(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var lecteur = new StreamReader(flux, System.Text.Encoding.UTF8))
                texte = lecteur.ReadToEnd();
            if (!texte.Contains(Marque, StringComparison.Ordinal)) return null;
            var resultat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ligne in texte.Split('\n'))
            {
                var l = ligne.TrimEnd('\r');
                var egal = l.IndexOf('=');
                if (egal > 0) resultat.TryAdd(l[..egal], l[(egal + 1)..]);
            }

            return resultat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private List<RomPackIndexEntry> Paquets()
    {
        try
        {
            var info = new FileInfo(_index);
            if (!info.Exists) return [];
            if (_indexLu is { } lu && lu.Ticks == info.LastWriteTimeUtc.Ticks && lu.Taille == info.Length) return lu.Paquets;
            var paquets = JsonSerializer.Deserialize<RomPackInstallerIndex>(File.ReadAllText(_index), LectureDeLIndex)?.Packs ?? [];
            _indexLu = (info.LastWriteTimeUtc.Ticks, info.Length, paquets);
            return paquets;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private Cache Lire()
    {
        if (_memoire is not null) return _memoire;
        try
        {
            _memoire = File.Exists(_cache) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(_cache)) ?? new Cache() : new Cache();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _memoire = new Cache();
        }

        return _memoire;
    }

    private void Enregistrer(Cache cache)
    {
        try
        {
            // Les versions d'un paquet que l'index ne connait plus n'ont plus rien a dire : elles sortent du cache.
            var connus = Paquets().Select(p => p.Sha256).Where(h => !string.IsNullOrWhiteSpace(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (connus.Count > 0)
            {
                foreach (var perime in cache.Entrees.Keys.Where(k => !connus.Contains(k.Split('|')[0])).ToList())
                    cache.Entrees.Remove(perime);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
            var provisoire = _cache + ".tmp";
            File.WriteAllText(provisoire, JsonSerializer.Serialize(cache, EcritureDuCache));
            File.Move(provisoire, _cache, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogWarning(ex, "World Scoring : cache des empreintes non ecrit");
        }
    }

    private sealed class Cache
    {
        /// <summary>Par « sha256 du paquet|entree » (« sha256|* » : tout le paquet, pour un delai depasse).</summary>
        [JsonPropertyName("entries")]
        public Dictionary<string, Entree> Entrees { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Entree
    {
        [JsonPropertyName("md5")]
        public string? Md5 { get; init; }

        [JsonPropertyName("headerless")]
        public string? SansEnTete { get; init; }

        /// <summary>Un echec garde (delai, plafond) : retente apres <see cref="OubliDUnEchec"/> ou avec un autre paquet.</summary>
        [JsonPropertyName("failure")]
        public string? Echec { get; init; }

        [JsonPropertyName("package")]
        public string Paquet { get; init; } = string.Empty;

        [JsonPropertyName("read_at")]
        public DateTime LuLe { get; init; }
    }
}
