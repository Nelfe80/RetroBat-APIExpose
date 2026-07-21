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

    private sealed record DbIndexes(
        Dictionary<string, CanonicalGame> ByHash,
        Dictionary<string, CanonicalGame> ByName);

    private readonly ConcurrentDictionary<string, Lazy<DbIndexes>> _dbIndexes =
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

        var db = GetIndexes(systemId);
        if (db.ByHash.TryGetValue(normalizedHash, out var canonical))
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

    /// <summary>Fiche canonique par NOM DE FICHIER ROM (tags de dump inclus,
    /// « Zool - Ninja of the Nth Dimension (Europe) ») — identite DAT du dump,
    /// pas un nom d'affichage. C'est la couche qui garantit la couverture des
    /// packs : les roms installees et la base viennent du meme ecosysteme, le
    /// nom de fichier y est canonique. Null si inconnu.</summary>
    public CanonicalGame? ResolveByRomName(string systemId, string? romFileName)
    {
        var stem = Path.GetFileNameWithoutExtension((romFileName ?? string.Empty).Replace('\\', '/')).Trim();
        if (string.IsNullOrWhiteSpace(systemId) || stem.Length == 0)
        {
            return null;
        }

        return GetIndexes(systemId).ByName.TryGetValue(Slugify(stem), out var canonical) ? canonical : null;
    }

    private DbIndexes GetIndexes(string systemId) => _dbIndexes.GetOrAdd(
        systemId.Trim().ToLowerInvariant(),
        key => new Lazy<DbIndexes>(() => LoadIndexes(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    // ── couche 1 : base ROM consolidée ───────────────────────────────────────

    private DbIndexes LoadIndexes(string normalizedSystem)
    {
        var byHash = new Dictionary<string, CanonicalGame>(StringComparer.Ordinal);
        var byName = new Dictionary<string, CanonicalGame>(StringComparer.Ordinal);
        var indexes = new DbIndexes(byHash, byName);
        var groupsRoot = Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "systems");
        var dataPath = ResolveDataFile(groupsRoot, normalizedSystem);
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return indexes;
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

                // PONTAGE PAR HASH : la base contient des entrees dupliquees du
                // meme dump avec des groupes DIFFERENTS (« Zool (Gremlin)
                // (Europe) » grp=zool et « Zool - Ninja... (Europe) »
                // grp=zool-ninja..., meme md5). Si un des hash de l'entree est
                // deja indexe, SA fiche fait foi pour toute l'entree — un meme
                // dump donne la meme cle, qu'on le resolve par hash ou par nom.
                var hashes = ReadHashes(entry).ToList();
                foreach (var hash in hashes)
                {
                    if (byHash.TryGetValue(hash, out var existing))
                    {
                        canonical = existing;
                        break;
                    }
                }

                foreach (var hash in hashes)
                {
                    byHash.TryAdd(hash, canonical);
                }

                foreach (var identity in ReadDatIdentities(entry))
                {
                    var slug = Slugify(identity);
                    if (slug.Length > 0)
                    {
                        byName.TryAdd(slug, canonical);
                    }
                }
            }

            _logger?.LogInformation(
                "Index canonique {System} : {Hashes} hash, {Names} identites DAT ({Path}).",
                normalizedSystem, byHash.Count, byName.Count, Path.GetFileName(dataPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Index canonique illisible pour {System}.", normalizedSystem);
            byHash.Clear();
            byName.Clear();
        }

        return indexes;
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
        var slug = Slugify(kind.Equals("game", StringComparison.OrdinalIgnoreCase)
            ? (grp.Length > 0 ? grp : id)
            : (id.Length > 0 ? id : grp));
        if (slug.Length == 0)
        {
            return null;
        }

        return new CanonicalGame(
            $"{system}/{slug}",
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

    // ── capture de score disponible ? ────────────────────────────────────────

    private sealed record ScoreIndex(Dictionary<string, string> AliasToSlug, HashSet<string> MemStems);

    private readonly ConcurrentDictionary<string, Lazy<ScoreIndex>> _scoreIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Le score de ce jeu est-il CAPTURABLE sur cette machine ? Vrai
    /// s'il existe une definition .MEM (resources/ram/&lt;system&gt;) pour ce
    /// dump — cherchee par md5, hash RA, nom de fichier puis slug direct.
    /// C'est ce drapeau qui permet a l'app joueur d'afficher « pas de record
    /// possible » AVANT de lancer, au lieu de frustrer apres la partie.</summary>
    public bool HasScoreDefinition(string systemId, string? romFileName, string? md5, string? cheevosHash)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return false;
        }

        var index = _scoreIndexes.GetOrAdd(
            systemId.Trim().ToLowerInvariant(),
            key => new Lazy<ScoreIndex>(() => LoadScoreIndex(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (index.MemStems.Count == 0)
        {
            return false;
        }

        // Les alias portent parfois une ponctuation que le nom de fichier n'a
        // pas (« 'Nth' ») : tout se compare aussi en SLUG. Et le stem sans ses
        // tags de dump (« (Europe) ») matche le .MEM directement.
        var stem = Path.GetFileNameWithoutExtension((romFileName ?? "").Replace('\\', '/')).Trim();
        var cut = stem.IndexOfAny(['(', '[']);
        var stemNoTags = cut > 0 ? stem[..cut].TrimEnd() : stem;
        foreach (var probe in new[] { md5, cheevosHash, stem, Slugify(stem), Slugify(stemNoTags) })
        {
            var key = (probe ?? "").Trim().ToLowerInvariant();
            if (key.Length > 0 && index.AliasToSlug.TryGetValue(key, out var slug) &&
                index.MemStems.Contains(slug))
            {
                return true;
            }
        }

        // Repli : le slug du fichier (avec ou sans tags) EST un .MEM.
        return (stem.Length > 0 && index.MemStems.Contains(Slugify(stem))) ||
               (stemNoTags.Length > 0 && index.MemStems.Contains(Slugify(stemNoTags)));
    }

    private ScoreIndex LoadScoreIndex(string normalizedSystem)
    {
        var aliasToSlug = new Dictionary<string, string>(StringComparer.Ordinal);
        var memStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Le dossier ram des jeux arcade s'appelle « arcade », quel que soit le
        // systeme RetroBat (mame, fbneo…).
        var folder = normalizedSystem is "mame" or "mame64" or "fbneo" or "fba" ? "arcade" : normalizedSystem;
        var ramRoot = Path.Combine(RetroBatPaths.PluginRoot, "resources", "ram", folder);
        if (!Directory.Exists(ramRoot))
        {
            return new ScoreIndex(aliasToSlug, memStems);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(ramRoot, "*.MEM"))
            {
                memStems.Add(Path.GetFileNameWithoutExtension(file));
            }

            var aliasPath = Path.Combine(ramRoot, "alias.json");
            if (File.Exists(aliasPath))
            {
                using var parsed = JsonDocument.Parse(File.ReadAllText(aliasPath));
                foreach (var property in parsed.RootElement.EnumerateObject())
                {
                    var slug = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(slug))
                    {
                        aliasToSlug.TryAdd(property.Name.Trim().ToLowerInvariant(), slug.Trim());
                        // Cle slugifiee AUSSI : la ponctuation des alias
                        // (« 'Nth' ») differe des noms de fichiers.
                        var slugKey = Slugify(property.Name);
                        if (slugKey.Length > 0)
                        {
                            aliasToSlug.TryAdd(slugKey, slug.Trim());
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Index .MEM illisible pour {System}.", normalizedSystem);
        }

        return new ScoreIndex(aliasToSlug, memStems);
    }

    /// <summary>Identites DAT d'une entree (fn/id/set + alias aka). Le nom
    /// d'affichage « n » n'indexe JAMAIS (doctrine : pas une identite).</summary>
    private static IEnumerable<string> ReadDatIdentities(JsonObject entry)
    {
        foreach (var field in new[] { "fn", "id", "set" })
        {
            var value = ReadString(entry, field);
            if (value.Length > 0)
            {
                yield return value;
            }
        }

        if (entry.TryGetPropertyValue("aka", out var akaNode) && akaNode is JsonArray aliases)
        {
            foreach (var aliasNode in aliases)
            {
                if (aliasNode is not JsonObject alias)
                {
                    continue;
                }

                foreach (var field in new[] { "fn", "id", "set" })
                {
                    var value = ReadString(alias, field);
                    if (value.Length > 0)
                    {
                        yield return value;
                    }
                }
            }
        }
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

    /// <summary>Normalise un identifiant de la base (« 64th._Street:_A_… ») en
    /// slug sur — minuscules, alphanumerique et tirets — pour que la cle
    /// traverse URL, SQL et attributs HTML sans surprise, dans le meme charset
    /// que les cles de repli « systeme/slug-du-fichier ».</summary>
    private static string Slugify(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug;
    }

    private static string ReadString(JsonObject entry, string field)
        => entry.TryGetPropertyValue(field, out var node) && node is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
}
