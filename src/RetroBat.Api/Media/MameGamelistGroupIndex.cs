using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed record SystemRelatedRom(string Rom, IReadOnlyList<string> Regions, IReadOnlyList<string> Languages);

public class MameGamelistGroupIndex
{
    private readonly Lazy<Dictionary<string, string>> _systemAliases;
    private readonly ConcurrentDictionary<string, Lazy<SystemGroupLookup>> _groupsByRomByFile;
    private readonly ILogger<MameGamelistGroupIndex>? _logger;

    public MameGamelistGroupIndex(ILogger<MameGamelistGroupIndex>? logger = null)
    {
        _logger = logger;
        _systemAliases = new Lazy<Dictionary<string, string>>(LoadSystemAliases, isThreadSafe: true);
        _groupsByRomByFile = new ConcurrentDictionary<string, Lazy<SystemGroupLookup>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Un fichier de base vient d'etre remplace (Data Pack) : l'index en memoire ne dit plus la
    /// verite, on le lache ; la prochaine fiche le reconstruit, et le cache binaire, date sur
    /// la source, se refait de lui-meme.
    /// </summary>
    public void Oublier(string jsonlFile)
    {
        _groupsByRomByFile.TryRemove(jsonlFile, out _);
    }

    public IReadOnlyList<string> GetRelatedRoms(string systemId, string gamePath, string? gameSlug = null)
    {
        return GetRelatedRomEntries(systemId, gamePath, gameSlug)
            .Select(entry => entry.Rom)
            .ToArray();
    }

    public IReadOnlyList<SystemRelatedRom> GetRelatedRomEntries(string systemId, string gamePath, string? gameSlug = null)
    {
        var jsonlFile = ResolveJsonlFile(systemId);
        if (string.IsNullOrWhiteSpace(jsonlFile))
        {
            return Array.Empty<SystemRelatedRom>();
        }

        var lookupKeys = BuildLookupKeys(gamePath, gameSlug);
        if (lookupKeys.Count == 0)
        {
            return Array.Empty<SystemRelatedRom>();
        }

        var groupsByRom = _groupsByRomByFile
            .GetOrAdd(jsonlFile, file => new Lazy<SystemGroupLookup>(() => LoadGroupsByRom(file), isThreadSafe: true))
            .Value;

        foreach (var key in lookupKeys)
        {
            if (groupsByRom.ExactByKey.TryGetValue(key, out var related))
            {
                return BuildRelatedEntries(related, groupsByRom.MetadataByRelated);
            }
        }

        if (IsMameLikeSystem(systemId))
        {
            foreach (var key in lookupKeys)
            {
                var shortKey = BuildShortTitleSlug(key);
                if (groupsByRom.ShortTitleByKey.TryGetValue(shortKey, out var related))
                {
                    return BuildRelatedEntries(related, groupsByRom.MetadataByRelated);
                }
            }
        }

        return Array.Empty<SystemRelatedRom>();
    }

    private SystemGroupLookup LoadGroupsByRom(string dataFile)
    {
        var exactIndex = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var shortTitleIndex = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var metadataByRelated = new Dictionary<string, CompactRomMetadata>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(GetSystemGroupsRoot(), dataFile);
        if (!File.Exists(path))
        {
            _logger?.LogInformation("System gamelist group index skipped: file not found at {Path}.", path);
            return new SystemGroupLookup(exactIndex, shortTitleIndex, metadataByRelated);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cached = TryReadCachedIndex(path);
        if (cached is not null)
        {
            _logger?.LogDebug(
                "INDEX CACHE hit file={File} | load={LoadMs}ms | lookupKeys={LookupKeyCount}, shortTitleKeys={ShortTitleKeyCount}, metadataEntries={MetadataCount}",
                Path.GetFileName(path),
                watch.ElapsedMilliseconds,
                cached.ExactByKey.Count,
                cached.ShortTitleByKey.Count,
                cached.MetadataByRelated.Count);
            return cached;
        }

        var groupCount = 0;
        var compactGroups = new Dictionary<string, SystemGroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (IsCompactEntry(root))
                {
                    AddCompactEntry(compactGroups, root);
                    continue;
                }

                var related = new List<string>();
                var relatedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lookupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var shortTitleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                AddString(root, "p", related, relatedSeen, lookupKeys, shortTitleKeys);
                AddString(root, "n", related, relatedSeen, lookupKeys, shortTitleKeys, addToRelated: false);
                AddString(root, "pref", related, relatedSeen, lookupKeys, shortTitleKeys);
                AddArray(root, "r", related, relatedSeen, lookupKeys, shortTitleKeys);
                AddArray(root, "cl", related, relatedSeen, lookupKeys, shortTitleKeys);
                AddArray(root, "pr", related, relatedSeen, lookupKeys, shortTitleKeys);

                var group = related
                    .Where(rom => !string.IsNullOrWhiteSpace(rom))
                    .ToArray();
                if (group.Length == 0 || lookupKeys.Count == 0)
                {
                    continue;
                }

                foreach (var key in lookupKeys)
                {
                    exactIndex[key] = group;
                }

                if (group.Length > 1)
                {
                    foreach (var key in shortTitleKeys)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !shortTitleIndex.ContainsKey(key))
                        {
                            shortTitleIndex[key] = group;
                        }
                    }
                }

                groupCount++;
            }
            catch (JsonException ex)
            {
                _logger?.LogDebug(ex, "Invalid system gamelist group line skipped.");
            }
        }

        var parseMs = watch.ElapsedMilliseconds;
        foreach (var group in compactGroups.Values)
        {
            var related = group.Related
                .Where(rom => !string.IsNullOrWhiteSpace(rom))
                .ToArray();
            if (related.Length == 0 || group.LookupKeys.Count == 0)
            {
                continue;
            }

            foreach (var key in group.LookupKeys)
            {
                exactIndex[key] = related;
            }

            if (related.Length > 1)
            {
                foreach (var key in group.ShortTitleKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key) && !shortTitleIndex.ContainsKey(key))
                    {
                        shortTitleIndex[key] = related;
                    }
                }
            }

            groupCount++;
            foreach (var pair in group.MetadataByRelated)
            {
                metadataByRelated.TryAdd(pair.Key, pair.Value);
            }
        }

        _logger?.LogDebug(
            "INDEX TIMING file={File} | parse={ParseMs}ms buildKeys={BuildMs}ms total={TotalMs}ms | groups={GroupCount}, lookupKeys={LookupKeyCount}, shortTitleKeys={ShortTitleKeyCount}, sizeMB={SizeMb:N1}",
            Path.GetFileName(path),
            parseMs,
            watch.ElapsedMilliseconds - parseMs,
            watch.ElapsedMilliseconds,
            groupCount,
            exactIndex.Count,
            shortTitleIndex.Count,
            new FileInfo(path).Length / 1048576.0);

        var lookup = new SystemGroupLookup(exactIndex, shortTitleIndex, metadataByRelated);
        WriteCachedIndex(path, lookup);
        return lookup;
    }

    private string ResolveJsonlFile(string systemId)
    {
        var normalizedSystem = NormalizeRomName(systemId);
        foreach (var fileName in BuildDataFileCandidates(normalizedSystem))
        {
            if (File.Exists(Path.Combine(GetSystemGroupsRoot(), fileName)))
            {
                return fileName;
            }
        }

        return _systemAliases.Value.TryGetValue(normalizedSystem, out var jsonlFile)
            ? jsonlFile
            : string.Empty;
    }

    private Dictionary<string, string> LoadSystemAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(GetSystemGroupsRoot(), "aliases.json");
        if (!File.Exists(path))
        {
            _logger?.LogInformation("System gamelist aliases skipped: file not found at {Path}.", path);
            return aliases;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return aliases;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.TryGetProperty("jsonl", out var jsonlElement) ||
                    jsonlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var systemId = NormalizeRomName(property.Name);
                var jsonlFile = jsonlElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(systemId) && !string.IsNullOrWhiteSpace(jsonlFile))
                {
                    aliases[systemId] = jsonlFile;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Invalid system gamelist aliases file skipped.");
        }

        return aliases;
    }

    private static List<string> BuildLookupKeys(string gamePath, string? gameSlug)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLookupKey(Path.GetFileNameWithoutExtension(gamePath ?? string.Empty), keys, seen);
        AddLookupKey(gameSlug, keys, seen);
        return keys;
    }

    private static void AddString(
        JsonElement root,
        string propertyName,
        ICollection<string> related,
        ISet<string> relatedSeen,
        ISet<string> lookupKeys,
        ISet<string> shortTitleKeys,
        bool addToRelated = true)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            AddRelatedValue(value, related, relatedSeen, lookupKeys, shortTitleKeys, addToRelated);
        }
    }

    private static void AddArray(
        JsonElement root,
        string propertyName,
        ICollection<string> related,
        ISet<string> relatedSeen,
        ISet<string> lookupKeys,
        ISet<string> shortTitleKeys)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in property.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.String)
            {
                AddRelatedValue(child.GetString(), related, relatedSeen, lookupKeys, shortTitleKeys);
            }
        }
    }

    private static bool IsCompactEntry(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("grp", out _) &&
            root.TryGetProperty("id", out _);
    }

    private static void AddCompactEntry(
        Dictionary<string, SystemGroupAccumulator> groups,
        JsonElement root)
    {
        var groupId = GetString(root, "grp");
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        if (!groups.TryGetValue(groupId, out var group))
        {
            group = new SystemGroupAccumulator();
            groups[groupId] = group;
        }

        var shareableRevisionAlias = IsStandardMediaShareCompactEntry(root);
        var metadata = ReadCompactMetadata(root);

        AddCompactRelatedString(root, "id", group, metadata);
        AddCompactRelatedString(root, "set", group, metadata);
        AddCompactRelatedString(root, "fn", group, metadata);
        AddString(root, "n", group.Related, group.RelatedSeen, group.LookupKeys, group.ShortTitleKeys, addToRelated: false);
        AddString(root, "sn", group.Related, group.RelatedSeen, group.LookupKeys, group.ShortTitleKeys, addToRelated: false);
        AddCompactAliases(root, group, shareableRevisionAlias, metadata);
        if (shareableRevisionAlias)
        {
            AddRevisionlessTitleAlias(root, "n", group, metadata);
            AddRevisionlessTitleAlias(root, "fn", group, metadata);
            AddKnownReleaseTitleAlias(root, "n", group, metadata);
            AddKnownReleaseTitleAlias(root, "fn", group, metadata);
        }
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static CompactRomMetadata ReadCompactMetadata(JsonElement root)
    {
        return new CompactRomMetadata(ReadStringArray(root, "reg"), ReadStringArray(root, "lang"));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return Array.Empty<string>();
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : [value];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in property.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = child.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static void AddRelatedValue(
        string? value,
        ICollection<string> related,
        ISet<string> relatedSeen,
        ISet<string> lookupKeys,
        ISet<string> shortTitleKeys,
        bool addToRelated = true)
    {
        var normalized = NormalizeRomName(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (addToRelated && relatedSeen.Add(normalized))
        {
            related.Add(normalized);
        }

        AddLookupKey(value, lookupKeys);
        var shortTitle = BuildShortTitleSlug(value);
        if (!string.IsNullOrWhiteSpace(shortTitle))
        {
            shortTitleKeys.Add(shortTitle);
        }
    }

    private static void AddMetadata(SystemGroupAccumulator group, string? value, CompactRomMetadata metadata)
    {
        var normalized = NormalizeRomName(value);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            (metadata.Regions.Count > 0 || metadata.Languages.Count > 0))
        {
            group.MetadataByRelated[normalized] = metadata;
        }
    }

    private static IReadOnlyList<SystemRelatedRom> BuildRelatedEntries(
        IReadOnlyList<string> related,
        IReadOnlyDictionary<string, CompactRomMetadata> metadataByRelated)
    {
        var entries = new List<SystemRelatedRom>(related.Count);
        foreach (var rom in related)
        {
            var key = NormalizeRomName(rom);
            var metadata = metadataByRelated.TryGetValue(key, out var found)
                ? found
                : CompactRomMetadata.Empty;
            entries.Add(new SystemRelatedRom(rom, metadata.Regions, metadata.Languages));
        }

        return entries;
    }

    private static bool IsMameLikeSystem(string systemId)
    {
        return (systemId ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "arcade" or "mame" or "mame64" or "fbneo" or "fba" or "hbmame" => true,
            _ => false
        };
    }

    private static IEnumerable<string> BuildDataFileCandidates(string normalizedSystem)
    {
        if (!string.IsNullOrWhiteSpace(normalizedSystem))
        {
            yield return $"{normalizedSystem}_lt.json";
        }

        if (normalizedSystem is "arcade" or "mame64" or "fba" or "hbmame")
        {
            yield return "mame_lt.json";
            yield return "arcade_lt.json";
        }
    }

    private static string GetSystemGroupsRoot()
    {
        return Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "systems");
    }

    // ----- built-index cache -----
    //
    // Building this index from the source costs ~21 s for fbneo (23 MB, 27 467 groups,
    // 179 679 lookup keys), and it is built lazily - so the first card a player opens
    // pays for it, once per API start. The result is a pure function of the source
    // file, so it is written out and read back instead.
    //
    // Groups are stored ONCE and the keys point at them by number: the same group
    // array is shared by every one of its keys, so writing it per key would inflate
    // the file 179 679 / 27 467 times over.
    //
    // Lives under .cache/, which is excluded from the packs: it is a derived artifact,
    // rebuilt on demand, never shipped.

    private const string IndexCacheMagic = "APXGRPIDX1";

    private static string GetIndexCacheRoot()
        => Path.Combine(RetroBatPaths.PluginRoot, ".cache", "gamelist-index");

    private static string BuildIndexCachePath(string sourcePath)
        => Path.Combine(GetIndexCacheRoot(), Path.GetFileNameWithoutExtension(sourcePath) + ".idx");

    private SystemGroupLookup? TryReadCachedIndex(string sourcePath)
    {
        var cachePath = BuildIndexCachePath(sourcePath);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var source = new FileInfo(sourcePath);
            using var stream = File.OpenRead(cachePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadString() != IndexCacheMagic ||
                reader.ReadInt64() != source.Length ||
                reader.ReadInt64() != source.LastWriteTimeUtc.Ticks)
            {
                return null; // the source moved on: rebuild
            }

            var groups = new string[reader.ReadInt32()][];
            for (var i = 0; i < groups.Length; i++)
            {
                var members = new string[reader.ReadInt32()];
                for (var m = 0; m < members.Length; m++)
                {
                    members[m] = reader.ReadString();
                }

                groups[i] = members;
            }

            Dictionary<string, string[]> ReadKeyMap()
            {
                var count = reader.ReadInt32();
                var map = new Dictionary<string, string[]>(count, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    var key = reader.ReadString();
                    map[key] = groups[reader.ReadInt32()];
                }

                return map;
            }

            var exact = ReadKeyMap();
            var shortTitle = ReadKeyMap();

            var metadataCount = reader.ReadInt32();
            var metadata = new Dictionary<string, CompactRomMetadata>(metadataCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < metadataCount; i++)
            {
                var key = reader.ReadString();
                var regions = new string[reader.ReadInt32()];
                for (var r = 0; r < regions.Length; r++)
                {
                    regions[r] = reader.ReadString();
                }

                var languages = new string[reader.ReadInt32()];
                for (var l = 0; l < languages.Length; l++)
                {
                    languages[l] = reader.ReadString();
                }

                metadata[key] = new CompactRomMetadata(regions, languages);
            }

            return new SystemGroupLookup(exact, shortTitle, metadata);
        }
        catch (Exception ex)
        {
            // a truncated or stale-format cache is never fatal: rebuild from source
            _logger?.LogDebug(ex, "Group index cache unusable for {Source}; rebuilding.", sourcePath);
            return null;
        }
    }

    private void WriteCachedIndex(string sourcePath, SystemGroupLookup lookup)
    {
        try
        {
            Directory.CreateDirectory(GetIndexCacheRoot());
            var source = new FileInfo(sourcePath);
            var cachePath = BuildIndexCachePath(sourcePath);
            var tempPath = cachePath + ".tmp";

            // group array instance → its number. The builder assigns the SAME array to
            // every key of a group, so reference identity is the grouping we want.
            var groupNumbers = new Dictionary<string[], int>(ReferenceEqualityComparer.Instance);
            foreach (var group in lookup.ExactByKey.Values.Concat(lookup.ShortTitleByKey.Values))
            {
                if (!groupNumbers.ContainsKey(group))
                {
                    groupNumbers[group] = groupNumbers.Count;
                }
            }

            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(IndexCacheMagic);
                writer.Write(source.Length);
                writer.Write(source.LastWriteTimeUtc.Ticks);

                writer.Write(groupNumbers.Count);
                foreach (var group in groupNumbers.OrderBy(pair => pair.Value).Select(pair => pair.Key))
                {
                    writer.Write(group.Length);
                    foreach (var member in group)
                    {
                        writer.Write(member);
                    }
                }

                void WriteKeyMap(Dictionary<string, string[]> map)
                {
                    writer.Write(map.Count);
                    foreach (var pair in map)
                    {
                        writer.Write(pair.Key);
                        writer.Write(groupNumbers[pair.Value]);
                    }
                }

                WriteKeyMap(lookup.ExactByKey);
                WriteKeyMap(lookup.ShortTitleByKey);

                writer.Write(lookup.MetadataByRelated.Count);
                foreach (var pair in lookup.MetadataByRelated)
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value.Regions.Count);
                    foreach (var region in pair.Value.Regions)
                    {
                        writer.Write(region);
                    }

                    writer.Write(pair.Value.Languages.Count);
                    foreach (var language in pair.Value.Languages)
                    {
                        writer.Write(language);
                    }
                }
            }

            File.Move(tempPath, cachePath, overwrite: true); // never a half-written cache
        }
        catch (Exception ex)
        {
            // failing to CACHE must never break the feature it accelerates
            _logger?.LogDebug(ex, "Group index cache not written for {Source}.", sourcePath);
        }
    }

    private static void AddCompactAliases(
        JsonElement root,
        SystemGroupAccumulator group,
        bool shareableRevisionAlias,
        CompactRomMetadata metadata)
    {
        if (!root.TryGetProperty("aka", out var aliases) || aliases.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var alias in aliases.EnumerateArray())
        {
            if (alias.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            AddCompactRelatedString(alias, "id", group, metadata);
            AddCompactRelatedString(alias, "set", group, metadata);
            AddCompactRelatedString(alias, "fn", group, metadata);
            AddString(alias, "n", group.Related, group.RelatedSeen, group.LookupKeys, group.ShortTitleKeys, addToRelated: false);
            if (shareableRevisionAlias)
            {
                AddRevisionlessTitleAlias(alias, "n", group, metadata);
                AddRevisionlessTitleAlias(alias, "fn", group, metadata);
                AddKnownReleaseTitleAlias(alias, "n", group, metadata);
                AddKnownReleaseTitleAlias(alias, "fn", group, metadata);
            }
        }
    }

    private static void AddCompactRelatedString(
        JsonElement root,
        string propertyName,
        SystemGroupAccumulator group,
        CompactRomMetadata metadata)
    {
        var value = GetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddRelatedValue(value, group.Related, group.RelatedSeen, group.LookupKeys, group.ShortTitleKeys);
        AddMetadata(group, value, metadata);
    }

    private static void AddRevisionlessTitleAlias(
        JsonElement root,
        string propertyName,
        SystemGroupAccumulator group,
        CompactRomMetadata metadata)
    {
        var title = BuildRevisionlessTitleAlias(GetString(root, propertyName));
        if (!string.IsNullOrWhiteSpace(title))
        {
            AddRelatedValue(title, group.Related, group.RelatedSeen, group.LookupKeys, group.ShortTitleKeys);
            AddMetadata(group, title, metadata);
        }
    }

    private static bool IsStandardMediaShareCompactEntry(JsonElement root)
    {
        var type = GetString(root, "t");
        if (!string.IsNullOrWhiteSpace(type) &&
            !string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rank = GetString(root, "rk");
        if (IsNonStandardMediaShareFlag(rank))
        {
            return false;
        }

        if (root.TryGetProperty("flg", out var flags) && flags.ValueKind == JsonValueKind.Array)
        {
            foreach (var flag in flags.EnumerateArray())
            {
                if (flag.ValueKind == JsonValueKind.String && IsNonStandardMediaShareFlag(flag.GetString()))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsNonStandardMediaShareFlag(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "hack" or "fan_translation" or "translation" or "pirate" or "bootleg" or
            "prototype" or "proto" or "demo" or "beta" or "alpha" or "homebrew" or
            "aftermarket" or "unlicensed" or "trainer" or "cheat" => true,
            _ => false
        };
    }

    private static string BuildRevisionlessTitleAlias(string? value)
    {
        var cleaned = RemoveBracketedContent(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = Regex.Replace(cleaned, @"\bRev(?:ision)?\s+PRG\s*[A-Za-z0-9._-]+\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\bPRG\s*[A-Za-z0-9._-]+\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\bRev(?:ision)?\s*[A-Za-z0-9._-]+\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+,", ",");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Trim(' ', '-', '_', ',');
    }

    private static void AddKnownReleaseTitleAlias(
        JsonElement root,
        string propertyName,
        SystemGroupAccumulator group,
        CompactRomMetadata metadata)
    {
        var alias = BuildKnownReleaseTitleAlias(GetString(root, propertyName));
        if (!string.IsNullOrWhiteSpace(alias))
        {
            AddRelatedValue(alias, group.Related, group.RelatedSeen, group.LookupKeys, group.ShortTitleKeys);
            AddMetadata(group, alias, metadata);
        }
    }

    private static string BuildKnownReleaseTitleAlias(string? value)
    {
        var cleaned = RemoveBracketedContent(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = Regex.Replace(cleaned, @"\s+-\s+The\s+Video\s+Game$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Trim(' ', '-', '_', ',');
    }

    private static void AddLookupKey(string? value, ICollection<string> keys, ISet<string> seen)
    {
        foreach (var candidate in BuildLookupKeyCandidates(value))
        {
            if (seen.Add(candidate))
            {
                keys.Add(candidate);
            }
        }
    }

    private static void AddLookupKey(string? value, ISet<string> keys)
    {
        foreach (var candidate in BuildLookupKeyCandidates(value))
        {
            keys.Add(candidate);
        }
    }

    private static IEnumerable<string> BuildLookupKeyCandidates(string? value)
    {
        var normalized = NormalizeRomName(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        var slug = BuildSlug(value);
        if (!string.IsNullOrWhiteSpace(slug) &&
            !string.Equals(slug, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return slug;
        }

        var compactSlug = BuildCompactSlug(slug);
        if (!string.IsNullOrWhiteSpace(compactSlug) &&
            !string.Equals(compactSlug, slug, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(compactSlug, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return compactSlug;
        }
    }

    private static string BuildSlug(string? value)
    {
        var cleaned = RemoveBracketedContent(value ?? string.Empty);
        var builder = new System.Text.StringBuilder();
        var lastWasSeparator = false;
        foreach (var character in cleaned.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('_');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug;
    }

    private static string RemoveBracketedContent(string value)
    {
        var builder = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var character in value)
        {
            if (character is '(' or '[')
            {
                depth++;
                continue;
            }

            if (character is ')' or ']')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
            }

            if (depth == 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static string NormalizeRomName(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    public static string NormalizeRomSlug(string? value)
    {
        return BuildSlug(value);
    }

    public static string NormalizeCompactRomSlug(string? value)
    {
        return BuildCompactSlug(BuildSlug(value));
    }

    public static string NormalizeShortRomSlug(string? value)
    {
        return BuildShortTitleSlug(value);
    }

    public static string NormalizeCompactShortRomSlug(string? value)
    {
        return BuildCompactSlug(BuildShortTitleSlug(value));
    }

    private static string BuildShortTitleSlug(string? value)
    {
        var cleaned = RemoveBracketedContent(value ?? string.Empty).Trim();
        var separators = new[] { " - ", " : ", ": " };
        foreach (var separator in separators)
        {
            var index = cleaned.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                cleaned = cleaned[..index];
                break;
            }
        }

        return BuildSlug(cleaned);
    }

    private static string BuildCompactSlug(string? slug)
    {
        return new string((slug ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    }

    private sealed record SystemGroupLookup(
        Dictionary<string, string[]> ExactByKey,
        Dictionary<string, string[]> ShortTitleByKey,
        Dictionary<string, CompactRomMetadata> MetadataByRelated);

    private sealed record CompactRomMetadata(IReadOnlyList<string> Regions, IReadOnlyList<string> Languages)
    {
        public static CompactRomMetadata Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
    }

    private sealed class SystemGroupAccumulator
    {
        public List<string> Related { get; } = new();
        public HashSet<string> RelatedSeen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> LookupKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ShortTitleKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CompactRomMetadata> MetadataByRelated { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
