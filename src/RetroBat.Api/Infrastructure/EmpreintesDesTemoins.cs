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
/// </summary>
public sealed class EmpreintesDesTemoins
{
    /// <summary>Au-dela, ce n'est plus une cartouche : on n'extrait pas un disque pour une collection.</summary>
    internal const long PlafondDeLEntree = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions LectureDeLIndex = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions EcritureDuCache = new() { WriteIndented = true };
    private const string Marque = "APIEXPOSE_ON_THE_FLY_ROM_PLACEHOLDER_v1";

    private readonly string _index;
    private readonly string _cache;
    private readonly Func<string, string, string, string, CancellationToken, Task<string>> _extraire;
    private readonly Func<string, string, long, (string? Md5, string? SansEnTete)> _empreintes;
    private readonly string _dossierProvisoire;
    private readonly ILogger? _journal;
    private readonly object _verrou = new();
    private Cache? _memoire;
    private readonly HashSet<string> _echecs = new(StringComparer.OrdinalIgnoreCase);
    private (long Ticks, long Taille, List<RomPackIndexEntry> Paquets)? _indexLu;

    public EmpreintesDesTemoins(ILogger<EmpreintesDesTemoins>? journal = null)
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
        Func<string, string, long, (string? Md5, string? SansEnTete)> empreintes, ILogger? journal = null)
    {
        _index = index;
        _cache = cache;
        _dossierProvisoire = dossierProvisoire;
        _extraire = extraire;
        _empreintes = empreintes;
        _journal = journal;
    }

    /// <summary>Vrai si ce fichier est un fichier d'attente du gestionnaire de ROM (petit, portant la marque). Pur hors lecture.</summary>
    public static bool EstUnTemoin(string chemin) => Lignes(chemin) is not null;

    /// <summary>
    /// L'empreinte du vrai contenu d'un fichier d'attente ; null si ce n'en est pas un. Une seule extraction a la fois ; les
    /// empreintes lues sont gardees, un echec n'est pas retente avant le prochain demarrage ou une nouvelle version du paquet.
    /// </summary>
    public EmpreintesDuTemoin? Lire(string chemin, string? systemeDuDossier)
    {
        var lignes = Lignes(chemin);
        if (lignes is null) return null;
        var systeme = lignes.GetValueOrDefault("system") is { Length: > 0 } s ? s : systemeDuDossier ?? string.Empty;
        var packPath = lignes.GetValueOrDefault("packPath") ?? string.Empty;
        var fichier = Path.GetFileName(chemin);

        var (paquet, rom) = Retrouver(systeme, packPath, fichier);
        if (paquet is null || rom is null)
            return new EmpreintesDuTemoin(null, null, "fichier d'attente sans jeu dans un paquet installé");

        var cle = paquet.Sha256 + "|" + rom.PackRelativePath;
        lock (_verrou)
        {
            if (Lire().Entrees.TryGetValue(cle, out var connu) && connu.Md5 is { Length: > 0 })
                return new EmpreintesDuTemoin(connu.Md5, connu.SansEnTete, null);
            if (_echecs.Contains(cle))
                return new EmpreintesDuTemoin(null, null, "ROM du pack illisible");

            var lu = Extraire(paquet, rom, systeme);
            if (lu is null)
            {
                _echecs.Add(cle);
                return new EmpreintesDuTemoin(null, null, "ROM du pack illisible");
            }

            var cache = Lire();
            cache.Entrees[cle] = new Entree { Md5 = lu.Value.Md5, SansEnTete = lu.Value.SansEnTete, LuLe = DateTime.UtcNow, Paquet = paquet.PackagePath };
            Enregistrer(cache);
            return new EmpreintesDuTemoin(lu.Value.Md5, lu.Value.SansEnTete, null);
        }
    }

    private (string? Md5, string? SansEnTete)? Extraire(RomPackIndexEntry paquet, RomPackRomEntry rom, string systeme)
    {
        if (rom.Size > PlafondDeLEntree || !File.Exists(paquet.PackagePath)) return null;
        var entree = !string.IsNullOrWhiteSpace(rom.ArchiveEntryPath) ? rom.ArchiveEntryPath : rom.PackRelativePath;
        if (string.IsNullOrWhiteSpace(entree)) return null;
        var dossier = Path.Combine(_dossierProvisoire, "empreinte-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dossier);
            using var delai = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            _journal?.LogInformation("World Scoring : lecture de {Entree} dans {Paquet} pour reconnaitre un fichier d'attente",
                entree, Path.GetFileName(paquet.PackagePath));
            var extrait = _extraire(paquet.PackagePath, entree, rom.PackRelativePath, dossier, delai.Token).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(extrait) || !File.Exists(extrait)) return null;
            var empreintes = _empreintes(extrait, systeme, PlafondDeLEntree);
            return empreintes.Md5 is { Length: > 0 } ? empreintes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            _journal?.LogWarning(ex, "World Scoring : entree {Entree} du paquet {Paquet} non lue", entree, Path.GetFileName(paquet.PackagePath));
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _journal?.LogWarning(ex, "World Scoring : dossier provisoire {Dossier} non efface", dossier);
            }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _memoire = new Cache();
        }

        return _memoire;
    }

    private void Enregistrer(Cache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
            File.WriteAllText(_cache, JsonSerializer.Serialize(cache, EcritureDuCache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal?.LogWarning(ex, "World Scoring : cache des empreintes non ecrit");
        }
    }

    private sealed class Cache
    {
        /// <summary>Par « sha256 du paquet|entree ».</summary>
        [JsonPropertyName("entries")]
        public Dictionary<string, Entree> Entrees { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Entree
    {
        [JsonPropertyName("md5")]
        public string? Md5 { get; init; }

        [JsonPropertyName("headerless")]
        public string? SansEnTete { get; init; }

        [JsonPropertyName("package")]
        public string Paquet { get; init; } = string.Empty;

        [JsonPropertyName("read_at")]
        public DateTime LuLe { get; init; }
    }
}
