using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed record RomMetadataResolution(
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    string Source);

public sealed class RomMetadataResolver
{
    private static readonly Regex TagRegex = new(@"[\(\[]([^\)\]]+)[\)\]]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonAlphaNumericRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] TagSeparators = [',', '/', ';', '+'];

    private readonly ConcurrentDictionary<string, Lazy<RomMetadataIndex>> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ApiExposeTaxonomyService _taxonomy;
    private readonly ILogger<RomMetadataResolver>? _logger;

    public RomMetadataResolver(ApiExposeTaxonomyService taxonomy, ILogger<RomMetadataResolver>? logger = null)
    {
        _taxonomy = taxonomy;
        _logger = logger;
    }

    public RomMetadataResolution Resolve(string systemId, string? gamePath, string? gameName)
    {
        var regions = new List<string>();
        var languages = new List<string>();
        var matchedCompactData = false;

        foreach (var value in new[] { gamePath, Path.GetFileNameWithoutExtension(NormalizePathSeparators(gamePath)), gameName })
        {
            AddExtractedRegions(regions, value);
            AddExtractedLanguages(languages, value);
        }

        var index = GetIndex(systemId);
        foreach (var key in BuildLookupKeys(gamePath, gameName))
        {
            if (!index.ByKey.TryGetValue(key, out var metadata))
            {
                continue;
            }

            matchedCompactData = true;
            AddDistinctRange(regions, metadata.Regions);
            AddDistinctRange(languages, metadata.Languages);
        }

        return new RomMetadataResolution(
            regions,
            languages,
            matchedCompactData
                ? "rom-set-manager"
                : regions.Count > 0 || languages.Count > 0
                    ? "rom-tags"
                    : string.Empty);
    }

    private RomMetadataIndex GetIndex(string systemId)
    {
        var normalizedSystem = NormalizeRomId(systemId);
        if (string.IsNullOrWhiteSpace(normalizedSystem))
        {
            return RomMetadataIndex.Empty;
        }

        return _indexes.GetOrAdd(
            normalizedSystem,
            key => new Lazy<RomMetadataIndex>(() => LoadIndex(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private RomMetadataIndex LoadIndex(string normalizedSystem)
    {
        var groupsRoot = Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "systems");
        var dataPath = ResolveRomSetDataFile(groupsRoot, normalizedSystem);
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return RomMetadataIndex.Empty;
        }

        try
        {
            var byKey = new Dictionary<string, RomMetadataEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in ReadDataEntries(dataPath))
            {
                var metadata = BuildMetadata(entry);
                if (metadata.IsEmpty)
                {
                    continue;
                }

                foreach (var key in ReadCompactMatchKeys(entry))
                {
                    if (!byKey.TryGetValue(key, out var existing))
                    {
                        byKey[key] = metadata;
                        continue;
                    }

                    byKey[key] = existing.Merge(metadata);
                }
            }

            return byKey.Count == 0 ? RomMetadataIndex.Empty : new RomMetadataIndex(byKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(
                ex,
                "Lecture des metadonnees Roms Manager ignoree pour system={SystemId}, path={DataPath}.",
                normalizedSystem,
                dataPath);
            return RomMetadataIndex.Empty;
        }
    }

    private RomMetadataEntry BuildMetadata(JsonObject entry)
    {
        var regions = new List<string>();
        var languages = new List<string>();

        foreach (var value in ReadStringArray(entry, "reg"))
        {
            AddNormalizedRegion(regions, value);
        }

        foreach (var value in ReadStringArray(entry, "lang"))
        {
            AddNormalizedLanguages(languages, value);
        }

        foreach (var value in new[] { ReadString(entry, "fn"), ReadString(entry, "n"), ReadString(entry, "id"), ReadString(entry, "set") })
        {
            AddExtractedRegions(regions, value);
            AddExtractedLanguages(languages, value);
        }

        if (entry.TryGetPropertyValue("aka", out var aliasesNode) && aliasesNode is JsonArray aliases)
        {
            foreach (var aliasNode in aliases)
            {
                if (aliasNode is not JsonObject alias)
                {
                    continue;
                }

                foreach (var value in new[] { ReadString(alias, "fn"), ReadString(alias, "n"), ReadString(alias, "id"), ReadString(alias, "set") })
                {
                    AddExtractedRegions(regions, value);
                    AddExtractedLanguages(languages, value);
                }
            }
        }

        return new RomMetadataEntry(regions, languages);
    }

    private void AddExtractedRegions(List<string> regions, string? value)
    {
        foreach (var token in ExtractTagTokens(value))
        {
            AddNormalizedRegion(regions, token);
        }
    }

    private void AddExtractedLanguages(List<string> languages, string? value)
    {
        foreach (var token in ExtractTagTokens(value))
        {
            AddNormalizedLanguages(languages, token);
        }

        if (!string.IsNullOrWhiteSpace(value) &&
            Regex.IsMatch(value, @"T[+-](Fre|Fr|French)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddDistinct(languages, "Fr");
        }
    }

    private void AddNormalizedRegion(List<string> regions, string? value)
    {
        var region = _taxonomy.NormalizeRomRegionToken(value ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(region))
        {
            AddDistinct(regions, region);
        }
    }

    private void AddNormalizedLanguages(List<string> languages, string? value)
    {
        foreach (var language in _taxonomy.NormalizeRomLanguageTokens(value ?? string.Empty))
        {
            AddDistinct(languages, language);
        }
    }

    private static IEnumerable<JsonObject> ReadDataEntries(string dataPath)
    {
        foreach (var line in File.ReadLines(dataPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (JsonNode.Parse(line) is JsonObject entry)
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<string> BuildLookupKeys(string? gamePath, string? gameName)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMatchKey(keys, seen, "rom", gamePath);
        AddMatchKey(keys, seen, "rom", Path.GetFileNameWithoutExtension(NormalizePathSeparators(gamePath)));
        AddMatchKey(keys, seen, "rom", gameName);
        return keys;
    }

    private static List<string> ReadCompactMatchKeys(JsonObject entry)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMatchKey(keys, seen, "rom", ReadString(entry, "id"));
        AddMatchKey(keys, seen, "rom", ReadString(entry, "set"));
        AddMatchKey(keys, seen, "rom", ReadString(entry, "fn"));
        AddMatchKey(keys, seen, "rom", ReadString(entry, "n"));

        if (entry.TryGetPropertyValue("aka", out var aliasesNode) && aliasesNode is JsonArray aliases)
        {
            foreach (var aliasNode in aliases)
            {
                if (aliasNode is not JsonObject alias)
                {
                    continue;
                }

                AddMatchKey(keys, seen, "rom", ReadString(alias, "id"));
                AddMatchKey(keys, seen, "rom", ReadString(alias, "set"));
                AddMatchKey(keys, seen, "rom", ReadString(alias, "fn"));
                AddMatchKey(keys, seen, "rom", ReadString(alias, "n"));
            }
        }

        return keys;
    }

    private static string ResolveRomSetDataFile(string groupsRoot, string systemId)
    {
        if (string.IsNullOrWhiteSpace(groupsRoot) || string.IsNullOrWhiteSpace(systemId))
        {
            return string.Empty;
        }

        var normalizedSystem = NormalizeRomId(systemId);
        var aliasPath = Path.Combine(groupsRoot, "aliases.json");
        if (File.Exists(aliasPath))
        {
            try
            {
                var aliases = JsonSerializer.Deserialize<Dictionary<string, RomSetSystemAlias>>(File.ReadAllText(aliasPath))
                    ?? new Dictionary<string, RomSetSystemAlias>(StringComparer.OrdinalIgnoreCase);
                if (aliases.TryGetValue(normalizedSystem, out var alias) && !string.IsNullOrWhiteSpace(alias.Jsonl))
                {
                    var path = Path.Combine(groupsRoot, alias.Jsonl);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch (JsonException)
            {
                // Fall back to direct file names when the alias manifest is malformed.
            }
        }

        foreach (var fileName in BuildRomSetDataFileCandidates(normalizedSystem))
        {
            var path = Path.Combine(groupsRoot, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> BuildRomSetDataFileCandidates(string normalizedSystem)
    {
        if (!string.IsNullOrWhiteSpace(normalizedSystem))
        {
            yield return $"{normalizedSystem}_lt.json";
            yield return $"{normalizedSystem}_lt.jsonl";
            yield return $"{normalizedSystem}.json";
            yield return $"{normalizedSystem}.jsonl";
        }

        if (normalizedSystem is "arcade" or "mame64")
        {
            yield return "mame_lt.json";
            yield return "mame_lt.jsonl";
            yield return "mame.json";
            yield return "mame.jsonl";
        }
    }

    private static List<string> ReadStringArray(JsonObject group, string field)
    {
        if (!group.TryGetPropertyValue(field, out var node) || node is not JsonArray array)
        {
            return new List<string>();
        }

        return array
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static string ReadString(JsonObject group, string field)
    {
        return group.TryGetPropertyValue(field, out var node) && node != null
            ? node.ToString()
            : string.Empty;
    }

    private static void AddMatchKey(List<string> keys, ISet<string> seen, string prefix, string? value)
    {
        var normalized = NormalizeRomId(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AddRawMatchKey(keys, seen, prefix + ":" + normalized);
        }
    }

    private static void AddRawMatchKey(List<string> keys, ISet<string> seen, string key)
    {
        if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
        {
            keys.Add(key);
        }
    }

    private static IEnumerable<string> ExtractTagTokens(string? rom)
    {
        if (string.IsNullOrWhiteSpace(rom))
        {
            yield break;
        }

        foreach (Match match in TagRegex.Matches(rom))
        {
            foreach (var token in match.Groups[1].Value.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    yield return token.Trim();
                }
            }
        }
    }

    private static string NormalizeRomId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = NormalizePathSeparators(normalized);
        normalized = Path.GetFileName(normalized);
        normalized = NormalizeKnownRomTags(normalized);
        normalized = NonAlphaNumericRegex.Replace(normalized, "-");
        return normalized.Trim('-');
    }

    private static string NormalizePathSeparators(string? value)
    {
        return (value ?? string.Empty).Replace('\\', '/');
    }

    private static string NormalizeKnownRomTags(string value)
    {
        var normalized = value;
        normalized = Regex.Replace(normalized, @"\((u)\)", "(usa)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\((e)\)", "(europe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\((j)\)", "(japan)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\((w)\)", "(world)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\[(?:!|a\d*|b\d*|o\d*|p\d*|h\d*|f\d*)\]", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return normalized;
    }

    private static void AddDistinctRange(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AddDistinct(target, value);
        }
    }

    private static void AddDistinct(List<string> target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

    private sealed record RomMetadataEntry(IReadOnlyList<string> Regions, IReadOnlyList<string> Languages)
    {
        public bool IsEmpty => Regions.Count == 0 && Languages.Count == 0;

        public RomMetadataEntry Merge(RomMetadataEntry other)
        {
            var regions = Regions.ToList();
            var languages = Languages.ToList();
            AddDistinctRange(regions, other.Regions);
            AddDistinctRange(languages, other.Languages);
            return new RomMetadataEntry(regions, languages);
        }
    }

    private sealed record RomMetadataIndex(IReadOnlyDictionary<string, RomMetadataEntry> ByKey)
    {
        public static RomMetadataIndex Empty { get; } = new(new Dictionary<string, RomMetadataEntry>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class RomSetSystemAlias
    {
        [JsonPropertyName("jsonl")]
        public string Jsonl { get; set; } = string.Empty;
    }
}
