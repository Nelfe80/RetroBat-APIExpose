using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Ce qu'EmulationStation déclare d'un système : où vivent ses ROMs, et quels cores libretro
/// il lui connaît. Les deux réponses sortent du même fichier et du même cache.
///
/// C'est ES qui décide, pas nous. Chaque machine est différente : un disque secondaire, un
/// partage réseau, une arborescence héritée d'une installation précédente. `es_systems.cfg`
/// déclare pour chaque système un &lt;path&gt;, et c'est la SEULE réponse qui vaille.
///
/// Supposer <c>roms/&lt;système&gt;</c> marche sur une installation par défaut et échoue chez tous
/// les autres, en donnant l'impression d'un replay incompatible alors que la ROM est simplement
/// ailleurs. C'est exactement le piège qu'on évite ici.
///
/// Le chemin déclaré est relatif à ES et peut contenir <c>~</c> (la racine EmulationStation) et
/// des <c>..</c> : on le résout entièrement avant de s'en servir.
/// </summary>
public sealed class EsSystemsRomPaths
{
    private readonly ILogger<EsSystemsRomPaths> _logger;
    private readonly object _gate = new();
    private DateTime _stamp;
    private Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, IReadOnlyList<string>> _coresByName = new(StringComparer.OrdinalIgnoreCase);

    public EsSystemsRomPaths(ILogger<EsSystemsRomPaths> logger) => _logger = logger;

    private static string ConfigPath => Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_systems.cfg");

    /// <summary>Racine à laquelle <c>~</c> se rapporte : le dossier EmulationStation, parent de
    /// <c>.emulationstation</c>.</summary>
    private static string EsHome => Path.GetFullPath(Path.Combine(RetroBatPaths.EmulationStationConfigRoot, ".."));

    /// <summary>Le dossier de ROMs déclaré pour ce système, ou null s'il n'existe pas.</summary>
    public string? DirectoryFor(string? systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName)) return null;
        var map = Load();

        if (map.TryGetValue(systemName, out var direct) && Directory.Exists(direct)) return direct;

        // Repli de tolerance : un manifeste d'avant R5 porte « mega_drive » la ou ES dit
        // « megadrive ». On compare donc en ignorant les separateurs, ce qui reste sans
        // ambiguite tant que deux systemes ne different que par un tiret.
        var reduit = Reduce(systemName);
        foreach (var (name, dir) in map)
            if (Reduce(name) == reduit && Directory.Exists(dir)) return dir;

        return null;
    }

    /// <summary>
    /// Les cores libretro qu'ES déclare pour ce système, dans SON ordre de préférence. C'est la
    /// table de correspondance exhaustive de la machine (244 systèmes ici) : la maintenir chez
    /// nous reviendrait à en tenir une seconde, qui vieillirait à part.
    ///
    /// Seuls les cores de l'émulateur « libretro » sont retenus : les autres émulateurs déclarés
    /// (bizhawk, ares, mednafen) nomment leurs propres modules, qui ne sont pas des DLL libretro.
    /// </summary>
    public IReadOnlyList<string> LibretroCoresFor(string? systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName)) return Array.Empty<string>();
        Load();
        var cores = _coresByName;

        if (cores.TryGetValue(systemName, out var direct)) return direct;

        var reduit = Reduce(systemName);
        foreach (var (name, liste) in cores)
            if (Reduce(name) == reduit) return liste;

        return Array.Empty<string>();
    }

    private static string Reduce(string s) => s.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private Dictionary<string, string> Load()
    {
        try
        {
            var fi = new FileInfo(ConfigPath);
            if (!fi.Exists) return _byName;
            lock (_gate)
            {
                if (fi.LastWriteTimeUtc == _stamp) return _byName;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var cores = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var system in XDocument.Load(ConfigPath).Descendants("system"))
                {
                    var name = system.Element("name")?.Value?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;

                    var declared = system.Element("path")?.Value?.Trim();
                    if (!string.IsNullOrEmpty(declared))
                    {
                        var resolved = Resolve(declared);
                        if (resolved is not null) map[name] = resolved;
                    }

                    var libretro = system.Descendants("emulator")
                        .Where(e => string.Equals((string?)e.Attribute("name"), "libretro", StringComparison.OrdinalIgnoreCase))
                        .Descendants("core")
                        .Select(c => c.Value.Trim())
                        .Where(c => c.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (libretro.Count > 0) cores[name] = libretro;
                }
                _byName = map;
                _coresByName = cores;
                _stamp = fi.LastWriteTimeUtc;
                _logger.LogDebug("Replay : {Count} systèmes lus dans es_systems.cfg.", map.Count);
                return _byName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay : es_systems.cfg illisible, emplacement des ROMs inconnu.");
            return _byName;
        }
    }

    private static string? Resolve(string declared)
    {
        try
        {
            var chemin = declared.Replace('/', Path.DirectorySeparatorChar);
            if (chemin.StartsWith("~", StringComparison.Ordinal))
                chemin = EsHome + chemin[1..];
            else if (!Path.IsPathRooted(chemin))
                chemin = Path.Combine(EsHome, chemin);
            return Path.GetFullPath(chemin); // resout les « .. »
        }
        catch { return null; }
    }
}
