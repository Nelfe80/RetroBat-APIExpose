using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Identité CANONIQUE d'un jeu à partir du hash de sa ROM.
///
/// Doctrine flotte : un jeu = système + contenu de la ROM — jamais un nom
/// (titre RetroAchievements ou nom d'affichage), sinon le même jeu existe sous
/// plusieurs clés (palmarès dédoublé, jeu non relançable depuis l'app joueur).
/// Le hash sert de clé de RECHERCHE ; l'identité renvoyée est l'entrée
/// canonique, qui regroupe tous les dumps du même jeu (USA/Europe/rev) et
/// unifie les systèmes partagés (genesis → megadrive).
///
/// Résolution en cascade :
///  1. base ROM consolidée (resources/gamelist/systems/*_lt.json) : hash
///     (md5/crc/sha1/RA) → « csys/grp » — les hacks/trainers gardent leur
///     propre identité (« csys/id »), leurs scores ne se mélangent pas au jeu
///     original ;
///  2. alias .MEM (resources/ram/&lt;system&gt;/alias.json) : md5 → slug de la
///     définition de score — cohérent par construction avec la chaîne de
///     capture (tous les dumps d'un groupe chargent le même .MEM) ;
///  3. rien : l'appelant retombe sur « système/slug-du-fichier ».
///
/// Index par système, chargé à la demande et mis en cache (même pattern que
/// RomMetadataResolver — encaisse le mame.json de ~60 000 entrées).
/// </summary>
public sealed class RomCanonicalResolver
{
    public sealed record CanonicalGame(
        string GameKey,
        string Name,
        string CanonicalSystem,
        string Kind,
        string Source);

    private readonly ConcurrentDictionary<string, Lazy<Dictionary<string, CanonicalGame>>> _dbIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> _memIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RomCanonicalResolver>? _logger;

    public RomCanonicalResolver(ILogger<RomCanonicalResolver>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Fiche canonique pour un hash (md5/crc/sha1, casse libre) — null
    /// si aucun des deux référentiels ne connaît ce dump.</summary>
    public CanonicalGame? Resolve(string systemId, string? hash)
    {
        var normalizedHash = (hash ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(systemId) || normalizedHash.Length == 0)
        {
            return null;
        }

        var db = _dbIndexes.GetOrAdd(
            systemId.Trim().ToLowerInvariant(),
            key => new Lazy<Dictionary<string, CanonicalGame>>(
                () => LoadDbIndex(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (db.TryGetValue(normalizedHash, out var canonical))
        {
            return canonical;
        }

        var mem = _memIndexes.GetOrAdd(
            systemId.Trim().ToLowerInvariant(),
            key => new Lazy<Dictionary<string, string>>(
                () => LoadMemAliasIndex(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (mem.TryGetValue(normalizedHash, out var memSlug))
        {
            return new CanonicalGame(
                $"{systemId.Trim().ToLowerInvariant()}/{memSlug}",
                Name: string.Empty,
                CanonicalSystem: systemId.Trim().ToLowerInvariant(),
                Kind: "game",
                Source: "mem-alias");
        }

        return null;
    }

    // ── couche 1 : base ROM consolidée ───────────────────────────────────────

    private Dictionary<string, CanonicalGame> LoadDbIndex(string normalizedSystem)
    {
        var index = new Dictionary<string, CanonicalGame>(StringComparer.Ordinal);
        var groupsRoot = Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "systems");
        var dataPath = ResolveDataFile(groupsRoot, normalizedSystem);
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return index;
        }

        try
        {
            foreach (var line in File.ReadLines(dataPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject? entry;
                try
                {
                    entry = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue; // une ligne illisible ne casse pas l'index
                }

                if (entry is null)
                {
                    continue;
                }

                var canonical = BuildCanonical(entry, normalizedSystem);
                if (canonical is null)
                {
                    continue;
                }

                foreach (var hash in ReadHashes(entry))
                {
                    // Premier arrivé, premier servi : un hash identifie UN dump,
                    // les collisions réelles sont inexistantes.
                    index.TryAdd(hash, canonical);
                }
            }

            _logger?.LogInformation(
                "Index canonique {System} : {Count} hash indexés depuis {Path}.",
                normalizedSystem, index.Count, Path.GetFileName(dataPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Index canonique illisible pour {System}.", normalizedSystem);
            index.Clear();
        }

        return index;
    }

    private static CanonicalGame? BuildCanonical(JsonObject entry, string requestedSystem)
    {
        var sys = ReadString(entry, "sys");
        var csys = ReadString(entry, "csys");
        var system = (csys.Length > 0 ? csys : sys).ToLowerInvariant();
        if (system.Length == 0)
        {
            system = requestedSystem;
        }

        var id = ReadString(entry, "id");
        var grp = ReadString(entry, "grp");
        var kind = ReadString(entry, "t");
        if (kind.Length == 0)
        {
            kind = "game";
        }

        // Un hack/trainer/proto garde SA clé (id) : ses scores ne rejoignent
        // jamais le palmarès du jeu original. Un jeu normal prend la clé de
        // GROUPE : tous les dumps (USA/Europe/rev) sont LE même jeu.
        var slug = kind.Equals("game", StringComparison.OrdinalIgnoreCase)
            ? (grp.Length > 0 ? grp : id)
            : (id.Length > 0 ? id : grp);
        if (slug.Length == 0)
        {
            return null;
        }

        return new CanonicalGame(
            $"{system}/{slug.ToLowerInvariant()}",
            Name: ReadString(entry, "n"),
            CanonicalSystem: system,
            Kind: kind.ToLowerInvariant(),
            Source: "gamelist-db");
    }

    private static IEnumerable<string> ReadHashes(JsonObject entry)
    {
        if (entry.TryGetPropertyValue("hsh", out var hshNode) && hshNode is JsonArray hashes)
        {
            foreach (var node in hashes)
            {
                if (node is not JsonObject hash)
                {
                    continue;
                }

                foreach (var field in new[] { "md5", "sha1", "crc" })
                {
                    var value = ReadString(hash, field).ToLowerInvariant();
                    if (value.Length > 0)
                    {
                        yield return value;
                    }
                }
            }
        }

        if (entry.TryGetPropertyValue("ra", out var raNode) && raNode is JsonObject ra)
        {
            var raHash = ReadString(ra, "h").ToLowerInvariant();
            if (raHash.Length > 0)
            {
                yield return raHash;
            }
        }
    }

    private static string ResolveDataFile(string groupsRoot, string normalizedSystem)
    {
        if (!Directory.Exists(groupsRoot))
        {
            return string.Empty;
        }

        // aliases.json : les systèmes partagés pointent le même fichier
        // (genesis → megadrive_lt.json).
        var aliasPath = Path.Combine(groupsRoot, "aliases.json");
        if (File.Exists(aliasPath))
        {
            try
            {
                using var parsed = JsonDocument.Parse(File.ReadAllText(aliasPath));
                if (parsed.RootElement.TryGetProperty(normalizedSystem, out var alias) &&
                    alias.TryGetProperty("jsonl", out var jsonl) &&
                    jsonl.GetString() is { Length: > 0 } fileName)
                {
                    var aliased = Path.Combine(groupsRoot, fileName);
                    if (File.Exists(aliased))
                    {
                        return aliased;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (var candidate in new[]
        {
            $"{normalizedSystem}_lt.json", $"{normalizedSystem}_lt.jsonl",
            $"{normalizedSystem}.json", $"{normalizedSystem}.jsonl"
        })
        {
            var path = Path.Combine(groupsRoot, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    // ── couche 2 : alias .MEM (md5 → slug de définition de score) ────────────

    private Dictionary<string, string> LoadMemAliasIndex(string normalizedSystem)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliasPath = Path.Combine(
            RetroBatPaths.PluginRoot, "resources", "ram", normalizedSystem, "alias.json");
        if (!File.Exists(aliasPath))
        {
            return index;
        }

        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(aliasPath));
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                // Seules les clés qui SONT des hash md5 (32 hex) nous
                // intéressent ici — les clés « nom de ROM » restent l'affaire
                // de la chaîne .MEM.
                var key = property.Name.Trim().ToLowerInvariant();
                if (key.Length != 32 || !key.All(Uri.IsHexDigit))
                {
                    continue;
                }

                var slug = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    index.TryAdd(key, slug.Trim().ToLowerInvariant());
                }
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "alias.json illisible pour {System}.", normalizedSystem);
        }

        return index;
    }

    private static string ReadString(JsonObject entry, string field)
        => entry.TryGetPropertyValue(field, out var node) && node is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
}
