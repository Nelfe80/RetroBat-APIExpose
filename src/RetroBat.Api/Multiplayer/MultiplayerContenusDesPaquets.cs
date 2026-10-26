using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LES JEUX DES PAQUETS EXTRAITS A LA VOLEE (26/10) : le gestionnaire de ROM laisse dans <c>roms/&lt;systeme&gt;/</c> un
/// fichier d'attente de 287 octets par jeu, et n'extrait le vrai jeu de son paquet (<c>package-installer/snes.rar</c>) qu'a
/// sa selection ou a son lancement par ES. La collection MULTIPLAYER reconnait un jeu console a son CONTENU (le md5 du
/// catalogue, jamais un nom) : un fichier d'attente ne se reconnaissait pas, et Contra III, Wild Guns, Goof Troop et Super
/// Mario Kart manquaient a la borne du user. L'index du gestionnaire ne garde pas ces empreintes.
///
/// Une fois par paquet (et a chaque nouvelle version du paquet), quand aucune partie ne tourne : le paquet est extrait en
/// entier dans un dossier provisoire sur son propre disque, le md5 du contenu de chaque jeu est garde
/// (<c>state/nelfeplay/contenus-des-paquets.json</c>), et le dossier est efface. Une partie qui commence arrete la lecture,
/// reprise plus tard.
/// </summary>
public sealed class MultiplayerContenusDesPaquets
{
    private static readonly JsonSerializerOptions LectureDeLIndex = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions EcritureDuCache = new() { WriteIndented = true };

    private readonly string _index;
    private readonly string _cache;
    private readonly Func<string, string, CancellationToken, Task> _extraire;
    private readonly Func<string, string?> _md5DuContenu;
    private readonly Func<string, bool> _estUnTemoin;
    private readonly ILogger<MultiplayerContenusDesPaquets>? _journal;
    private readonly object _verrou = new();
    private Cache? _memoire;
    private Task<bool>? _lecture;

    public MultiplayerContenusDesPaquets(ILogger<MultiplayerContenusDesPaquets>? journal = null)
        : this(RetroBatPaths.RomPackInstallerIndexPath, Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "contenus-des-paquets.json"),
            RomPackInstallerService.ExtraireToutAsync, NelfePlayScoringCollectionSyncService.Md5DuContenu, RomPackInstallerService.EstUnTemoin, journal)
    {
    }

    internal MultiplayerContenusDesPaquets(string index, string cache, Func<string, string, CancellationToken, Task> extraire,
        Func<string, string?> md5DuContenu, Func<string, bool> estUnTemoin, ILogger<MultiplayerContenusDesPaquets>? journal = null)
    {
        _index = index;
        _cache = cache;
        _extraire = extraire;
        _md5DuContenu = md5DuContenu;
        _estUnTemoin = estUnTemoin;
        _journal = journal;
    }

    /// <summary>Le md5 du contenu d'un fichier d'attente, d'apres les paquets deja lus ; null si ce n'en est pas un, ou inconnu.</summary>
    public string? Md5DuTemoin(string chemin)
    {
        if (!_estUnTemoin(chemin))
        {
            return null;
        }

        var systeme = Path.GetFileName(Path.GetDirectoryName(chemin)) ?? string.Empty;
        var fichier = Path.GetFileName(chemin);
        foreach (var paquet in Lire().Paquets)
        {
            if (string.Equals(paquet.Systeme, systeme, StringComparison.OrdinalIgnoreCase)
                && paquet.Jeux.TryGetValue(fichier, out var jeu) && jeu.Md5 is { Length: > 0 } md5)
            {
                return md5;
            }
        }

        return null;
    }

    /// <summary>Les paquets a la volee de ces systemes que la borne n'a pas encore lus (ou dont la version a change).</summary>
    public IReadOnlyList<RomPackIndexEntry> ALire(IReadOnlySet<string> systemes)
    {
        var lus = Lire().Paquets.Select(p => p.Empreinte).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Paquets().Where(p => p.OnTheFlyMode && systemes.Contains(p.SystemId) && File.Exists(p.PackagePath)
            && !lus.Contains(p.Sha256) && p.Roms.Count > 0).ToList();
    }

    /// <summary>
    /// Lit les paquets qui manquent, un seul a la fois et une seule lecture a la fois ; s'arrete si <paramref name="occupee"/>
    /// devient vrai (une partie commence). Vrai si des empreintes ont ete apprises.
    /// </summary>
    public Task<bool> LireAsync(IReadOnlySet<string> systemes, Func<bool> occupee, CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            if (_lecture is { IsCompleted: false })
            {
                return _lecture;
            }

            _lecture = Task.Run(() => LireLesPaquetsAsync(systemes, occupee, cancellationToken), cancellationToken);
            return _lecture;
        }
    }

    private async Task<bool> LireLesPaquetsAsync(IReadOnlySet<string> systemes, Func<bool> occupee, CancellationToken cancellationToken)
    {
        var appris = false;
        foreach (var paquet in ALire(systemes))
        {
            if (occupee() || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            appris |= await LireUnPaquetAsync(paquet, occupee, cancellationToken).ConfigureAwait(false);
        }

        return appris;
    }

    private async Task<bool> LireUnPaquetAsync(RomPackIndexEntry paquet, Func<bool> occupee, CancellationToken cancellationToken)
    {
        var dossierDuPaquet = Path.GetDirectoryName(paquet.PackagePath)!;
        var provisoire = Path.Combine(dossierDuPaquet, ".nelfeplay-lecture-" + Guid.NewGuid().ToString("N"));
        var disque = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dossierDuPaquet))!);
        // Le paquet entier sort sur le disque : il faut sa taille, une marge, et de quoi laisser vivre la borne.
        var besoin = (long)(paquet.Size * 1.3) + (2L << 30);
        if (disque.AvailableFreeSpace < besoin)
        {
            _journal?.LogInformation("MULTIPLAYER : paquet {Paquet} non lu, {Libre} Go libres pour {Besoin} Go",
                Path.GetFileName(paquet.PackagePath), disque.AvailableFreeSpace >> 30, besoin >> 30);
            return false;
        }

        using var arret = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Une partie qui commence arrete la lecture : le disque et le processeur sont a elle.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!arret.IsCancellationRequested)
                {
                    if (occupee())
                    {
                        arret.Cancel();
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), arret.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);
        try
        {
            _journal?.LogInformation("MULTIPLAYER : lecture du paquet {Paquet} ({Jeux} jeux) pour reconnaitre ses jeux",
                Path.GetFileName(paquet.PackagePath), paquet.Roms.Count);
            Directory.CreateDirectory(provisoire);
            await _extraire(paquet.PackagePath, provisoire, arret.Token).ConfigureAwait(false);
            var jeux = new Dictionary<string, JeuDuPaquet>(StringComparer.OrdinalIgnoreCase);
            foreach (var rom in paquet.Roms)
            {
                arret.Token.ThrowIfCancellationRequested();
                var fichier = Path.GetFileName(rom.DestinationFileName is { Length: > 0 } nom ? nom : rom.GamePath);
                if (Extrait(provisoire, rom) is { } extrait && _md5DuContenu(extrait) is { } md5)
                {
                    jeux[fichier] = new JeuDuPaquet { Md5 = md5, Taille = new FileInfo(extrait).Length };
                }
            }

            lock (_verrou)
            {
                var cache = Lire();
                var paquets = cache.Paquets.Where(p => !string.Equals(p.Paquet, paquet.PackagePath, StringComparison.OrdinalIgnoreCase)).ToList();
                paquets.Add(new PaquetLu
                {
                    Paquet = paquet.PackagePath,
                    Empreinte = paquet.Sha256,
                    Systeme = paquet.SystemId,
                    LuLe = DateTime.UtcNow,
                    Jeux = jeux,
                });
                _memoire = new Cache { Paquets = paquets };
                Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
                File.WriteAllText(_cache, JsonSerializer.Serialize(_memoire, EcritureDuCache));
            }

            _journal?.LogInformation("MULTIPLAYER : paquet {Paquet} lu, {Reconnus} jeux sur {Jeux}", Path.GetFileName(paquet.PackagePath),
                jeux.Count, paquet.Roms.Count);
            return jeux.Count > 0;
        }
        catch (OperationCanceledException)
        {
            _journal?.LogInformation("MULTIPLAYER : lecture du paquet {Paquet} arretee (une partie commence), reprise plus tard",
                Path.GetFileName(paquet.PackagePath));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _journal?.LogWarning(ex, "MULTIPLAYER : lecture du paquet {Paquet} en echec", Path.GetFileName(paquet.PackagePath));
            return false;
        }
        finally
        {
            arret.Cancel();
            try
            {
                if (Directory.Exists(provisoire))
                {
                    Directory.Delete(provisoire, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _journal?.LogWarning(ex, "MULTIPLAYER : dossier provisoire {Dossier} non efface", provisoire);
            }
        }
    }

    /// <summary>Le fichier extrait d'un jeu du paquet : a son chemin dans l'archive, sinon par son nom.</summary>
    private static string? Extrait(string provisoire, RomPackRomEntry rom)
    {
        foreach (var relatif in new[] { rom.ArchiveEntryPath, rom.PackRelativePath })
        {
            if (string.IsNullOrWhiteSpace(relatif))
            {
                continue;
            }

            var chemin = Path.Combine(provisoire, relatif.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
            if (File.Exists(chemin))
            {
                return chemin;
            }
        }

        var nom = Path.GetFileName(rom.DestinationFileName is { Length: > 0 } d ? d : rom.GamePath);
        return Directory.EnumerateFiles(provisoire, nom, SearchOption.AllDirectories).FirstOrDefault();
    }

    private IReadOnlyList<RomPackIndexEntry> Paquets()
    {
        try
        {
            return File.Exists(_index)
                ? JsonSerializer.Deserialize<RomPackInstallerIndex>(File.ReadAllText(_index), LectureDeLIndex)?.Packs ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private Cache Lire()
    {
        lock (_verrou)
        {
            if (_memoire is not null)
            {
                return _memoire;
            }

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
    }

    private sealed class Cache
    {
        [JsonPropertyName("packs")]
        public List<PaquetLu> Paquets { get; init; } = [];
    }

    private sealed class PaquetLu
    {
        [JsonPropertyName("package")]
        public string Paquet { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Empreinte { get; init; } = string.Empty;

        [JsonPropertyName("system")]
        public string Systeme { get; init; } = string.Empty;

        [JsonPropertyName("read_at")]
        public DateTime LuLe { get; init; }

        /// <summary>Par nom de fichier dans <c>roms/&lt;systeme&gt;/</c>.</summary>
        [JsonPropertyName("games")]
        public Dictionary<string, JeuDuPaquet> Jeux { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class JeuDuPaquet
    {
        [JsonPropertyName("md5")]
        public string? Md5 { get; init; }

        [JsonPropertyName("size")]
        public long Taille { get; init; }
    }
}
