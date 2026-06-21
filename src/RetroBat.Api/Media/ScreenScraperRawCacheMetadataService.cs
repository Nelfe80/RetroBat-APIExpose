using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class ScreenScraperRawCacheMetadataService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILogger<ScreenScraperRawCacheMetadataService>? _logger;

    public ScreenScraperRawCacheMetadataService(ILogger<ScreenScraperRawCacheMetadataService>? logger = null)
    {
        _logger = logger;
    }

    public async Task<bool> TryRebuildLanguageAsync(
        string systemId,
        string gameSlug,
        string requestedLanguage,
        CancellationToken cancellationToken = default)
    {
        var language = NormalizeLanguage(requestedLanguage);
        if (string.IsNullOrWhiteSpace(systemId) ||
            string.IsNullOrWhiteSpace(gameSlug) ||
            string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        var payloadPaths = BuildCachePayloadCandidates(systemId, gameSlug)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (payloadPaths.Count == 0)
        {
            return false;
        }

        foreach (var payloadPath in payloadPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.Open(payloadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!TryGetGameElement(document.RootElement, out var game))
                {
                    continue;
                }

                var fields = BuildFields(game, systemId, language);
                if (!HasUsefulLocalizedText(fields))
                {
                    continue;
                }

                await WriteBundleAsync(systemId, gameSlug, language, fields, cancellationToken);
                _logger?.LogInformation(
                    "Metadata rebuilt from local ScreenScraper raw cache: system={SystemId}, game={GameSlug}, language={Language}, payload={PayloadPath}.",
                    systemId,
                    gameSlug,
                    language,
                    payloadPath);
                return true;
            }
            catch (JsonException ex)
            {
                _logger?.LogDebug(ex, "Invalid ScreenScraper raw cache payload ignored: {PayloadPath}", payloadPath);
            }
            catch (IOException ex)
            {
                _logger?.LogDebug(ex, "ScreenScraper raw cache payload locked/ignored: {PayloadPath}", payloadPath);
            }
        }

        return false;
    }

    public async Task<ScreenScraperRawCacheMetadataRebuildResult> RebuildAsync(
        IReadOnlyCollection<string>? systemIds = null,
        IReadOnlyCollection<string>? gameSlugs = null,
        string? requestedLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var systems = ResolveRebuildSystems(systemIds);
        var slugFilter = gameSlugs == null || gameSlugs.Count == 0
            ? null
            : new HashSet<string>(gameSlugs.Where(slug => !string.IsNullOrWhiteSpace(slug)).Select(SafePathSegment), StringComparer.OrdinalIgnoreCase);
        var languageFilter = NormalizeLanguage(requestedLanguage);
        var result = new ScreenScraperRawCacheMetadataRebuildResult();

        foreach (var systemId in systems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemResult = await RebuildSystemAsync(systemId, slugFilter, languageFilter, cancellationToken);
            result.SystemResults.Add(systemResult);
            result.SystemsProcessed++;
            result.PayloadsScanned += systemResult.PayloadsScanned;
            result.PayloadsFailed += systemResult.PayloadsFailed;
            result.MetadataBundlesWritten += systemResult.MetadataBundlesWritten;
            result.MetadataBundlesSkipped += systemResult.MetadataBundlesSkipped;
        }

        return result;
    }

    private async Task<ScreenScraperRawCacheMetadataRebuildSystemResult> RebuildSystemAsync(
        string systemId,
        ISet<string>? slugFilter,
        string languageFilter,
        CancellationToken cancellationToken)
    {
        var result = new ScreenScraperRawCacheMetadataRebuildSystemResult { SystemId = systemId };
        foreach (var payload in EnumerateSystemPayloads(systemId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slugFilter != null && !slugFilter.Contains(payload.GameSlug))
            {
                continue;
            }

            result.PayloadsScanned++;
            try
            {
                await using var stream = File.Open(payload.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!TryGetGameElement(document.RootElement, out var game))
                {
                    result.MetadataBundlesSkipped++;
                    continue;
                }

                var languages = string.IsNullOrWhiteSpace(languageFilter)
                    ? ResolvePayloadLanguages(game)
                    : [languageFilter];
                foreach (var language in languages)
                {
                    var fields = BuildFields(game, systemId, language);
                    if (!HasUsefulLocalizedText(fields))
                    {
                        result.MetadataBundlesSkipped++;
                        continue;
                    }

                    await WriteBundleAsync(systemId, payload.GameSlug, language, fields, cancellationToken);
                    result.MetadataBundlesWritten++;
                }
            }
            catch (JsonException ex)
            {
                result.PayloadsFailed++;
                AddError(result, $"{payload.Path}: {ex.Message}");
            }
            catch (IOException ex)
            {
                result.PayloadsFailed++;
                AddError(result, $"{payload.Path}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                result.PayloadsFailed++;
                AddError(result, $"{payload.Path}: {ex.Message}");
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ResolveRebuildSystems(IReadOnlyCollection<string>? systemIds)
    {
        if (systemIds != null && systemIds.Count > 0)
        {
            return systemIds
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Select(systemId => systemId.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var root = ResolveRawCacheGamesRoot();
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                .Select(systemId => systemId!)
                .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    private static IEnumerable<ScreenScraperRawCachePayload> EnumerateSystemPayloads(string systemId)
    {
        var systemDirectory = Path.Combine(ResolveRawCacheGamesRoot(), SafePathSegment(systemId));
        if (!Directory.Exists(systemDirectory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(systemDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ScreenScraperRawCachePayload(
                Path.GetFileNameWithoutExtension(file),
                file);
        }

        foreach (var directory in Directory.EnumerateDirectories(systemDirectory)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var gameSlug = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(gameSlug))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                yield return new ScreenScraperRawCachePayload(gameSlug, file);
            }
        }
    }

    private static IReadOnlyList<string> ResolvePayloadLanguages(JsonElement game)
    {
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLocalizedDictionaryLanguages(languages, game, "synopsis");
        AddLocalizedCollectionLanguages(languages, game, "genres", "genre");
        AddLocalizedCollectionLanguages(languages, game, "familles", "famille");
        return languages
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddLocalizedDictionaryLanguages(ISet<string> languages, JsonElement game, string propertyName)
    {
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                AddLanguage(languages, property.Name);
                AddLanguage(languages, ReadString(property.Value, "langue", "lang", "language"));
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                AddLanguage(languages, ReadString(entry, "langue", "lang", "language"));
            }
        }
    }

    private static void AddLocalizedCollectionLanguages(ISet<string> languages, JsonElement game, string propertyName, string singularName)
    {
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return;
        }

        foreach (var item in EnumerateArrayOrSingle(node))
        {
            foreach (var nameNode in EnumerateArrayOrSingle(FirstExistingProperty(item, "noms", "names", singularName, "nom", "name")))
            {
                AddLanguage(languages, ReadString(nameNode, "langue", "lang", "language"));
            }
        }
    }

    private static void AddLanguage(ISet<string> languages, string? rawLanguage)
    {
        var language = NormalizeLanguage(rawLanguage);
        if (IsSupportedLanguage(language))
        {
            languages.Add(language);
        }
    }

    private static bool IsSupportedLanguage(string language)
    {
        return language is "cs" or "da" or "de" or "en" or "es" or "fi" or "fr" or "it" or "ja" or
            "ko" or "nl" or "pl" or "pt" or "ru" or "sv" or "tr" or "zh";
    }

    private static string ResolveRawCacheGamesRoot()
    {
        return Path.Combine(RetroBatPaths.MediaRoot, "scrap-cache", "screenscraper", "games");
    }

    private static IEnumerable<string> BuildCachePayloadCandidates(string systemId, string gameSlug)
    {
        foreach (var candidateSystemId in BuildCacheSystemCandidates(systemId))
        {
            var systemDirectory = Path.Combine(
                RetroBatPaths.MediaRoot,
                "scrap-cache",
                "screenscraper",
                "games",
                SafePathSegment(candidateSystemId));

            yield return Path.Combine(systemDirectory, $"{SafePathSegment(gameSlug)}.json");

            var legacyDirectory = Path.Combine(systemDirectory, SafePathSegment(gameSlug));
            if (Directory.Exists(legacyDirectory))
            {
                foreach (var legacyPayload in Directory.EnumerateFiles(legacyDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    yield return legacyPayload;
                }
            }
        }
    }

    private static IEnumerable<string> BuildCacheSystemCandidates(string systemId)
    {
        var normalized = (systemId ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        if (string.Equals(normalized, "arcade", StringComparison.OrdinalIgnoreCase))
        {
            yield return "mame";
            yield return "fbneo";
            yield return "fba";
            yield return "hbmame";
        }
        else if (string.Equals(normalized, "jaguar", StringComparison.OrdinalIgnoreCase))
        {
            yield return "atarijaguar";
        }
        else if (string.Equals(normalized, "jaguarcd", StringComparison.OrdinalIgnoreCase))
        {
            yield return "atarijaguarcd";
        }
        else if (string.Equals(normalized, "lynx", StringComparison.OrdinalIgnoreCase))
        {
            yield return "atarilynx";
        }
    }

    private static Dictionary<string, string> BuildFields(JsonElement game, string systemId, string language)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddField(fields, "name", ReadGameName(game), language);
        AddField(fields, "desc", ReadLocalizedDictionaryValue(game, "synopsis", language, "text", "value", "synopsis"), language);
        var rom = FirstExistingRom(game);
        AddField(fields, "releasedate", NormalizeReleaseDate(ReadReleaseDate(game)), language);
        AddField(fields, "developer", ReadLocalizedObjectString(game, "developpeur", "developer"), language);
        AddField(fields, "publisher", ReadLocalizedObjectString(game, "editeur", "publisher"), language);
        AddField(fields, "players", ReadNestedString(game, "joueurs", "text", "nombre", "players"), language);
        AddField(fields, "genre", ReadLocalizedCollection(game, "genres", "genre", "genre", language), language);
        AddField(fields, "family", ReadLocalizedCollection(game, "familles", "famille", "family", language), language);
        AddField(fields, "region", ReadRegion(game, rom, language), language);
        AddField(fields, "rating", NormalizeRating(ReadRating(game)), language);
        AddField(fields, "md5", ReadString(rom, "rommd5", "md5"), language);
        AddField(fields, "crc32", ReadString(rom, "romcrc", "crc", "crc32"), language);
        AddField(fields, "gameid", ReadString(game, "id", "idjeu", "ss_id"), language);
        AddField(fields, "system", systemId, language);
        fields["lang"] = ReadRomLanguageField(game, rom);
        AddField(fields, "source", "screenscraper", language);
        return fields;
    }

    private static bool HasUsefulLocalizedText(IReadOnlyDictionary<string, string> fields)
    {
        return HasNonEmpty(fields, "desc") || HasNonEmpty(fields, "genre") || HasNonEmpty(fields, "family");
    }

    private static bool HasNonEmpty(IReadOnlyDictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static async Task WriteBundleAsync(
        string systemId,
        string gameSlug,
        string language,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        var bundle = new LocalizedTextBundle
        {
            Language = language,
            UpdatedAtUtc = DateTime.UtcNow,
            Fields = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)
        };
        LocalizedMetadataBundleNormalizer.NormalizeBundleFields(bundle, language);

        var path = Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            systemId,
            "games",
            gameSlug,
            "texts",
            $"metadata-{language}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, bundle, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static void AddField(IDictionary<string, string> fields, string fieldName, string value, string language)
    {
        var normalizedField = LocalizedMetadataBundleNormalizer.NormalizeFieldName(fieldName);
        var sanitized = LocalizedMetadataSanitizer.SanitizeField(normalizedField, value, language);
        if (string.IsNullOrWhiteSpace(normalizedField) || string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        fields[normalizedField] = sanitized.Trim();
    }

    private static bool TryGetGameElement(JsonElement root, out JsonElement game)
    {
        game = default;
        if (!TryGetProperty(root, "response", out var response) ||
            (!TryGetProperty(response, "jeu", out game) && !TryGetProperty(response, "game", out game)))
        {
            return false;
        }

        if (game.ValueKind == JsonValueKind.Array && game.GetArrayLength() > 0)
        {
            game = game[0];
        }

        return game.ValueKind == JsonValueKind.Object;
    }

    private static string ReadGameName(JsonElement game)
    {
        if (TryGetProperty(game, "noms", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in new[] { "ss", "wor", "us", "eu", "jp" })
            {
                foreach (var entry in names.EnumerateArray())
                {
                    if (string.Equals(ReadString(entry, "region"), region, StringComparison.OrdinalIgnoreCase))
                    {
                        var value = ReadString(entry, "text", "value", "nom", "name");
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }

            foreach (var entry in names.EnumerateArray())
            {
                var value = ReadString(entry, "text", "value", "nom", "name");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return ReadString(game, "nom", "name");
    }

    private static string ReadLocalizedDictionaryValue(JsonElement game, string propertyName, string language, params string[] valueKeys)
    {
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return string.Empty;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (IsSameLanguage(property.Name, language) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? string.Empty;
                }

                var declared = ReadString(property.Value, "langue", "lang", "language");
                if (IsSameLanguage(declared, language))
                {
                    return ReadString(property.Value, valueKeys);
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                var declared = ReadString(entry, "langue", "lang", "language");
                if (IsSameLanguage(declared, language))
                {
                    return ReadString(entry, valueKeys);
                }
            }
        }

        return string.Empty;
    }

    private static string ReadLocalizedCollection(JsonElement game, string propertyName, string singularName, string fieldName, string language)
    {
        if (!TryGetProperty(game, propertyName, out var node))
        {
            return string.Empty;
        }

        var values = new List<string>();
        foreach (var item in EnumerateArrayOrSingle(node))
        {
            foreach (var nameNode in EnumerateArrayOrSingle(FirstExistingProperty(item, "noms", "names", singularName, "nom", "name")))
            {
                var declared = ReadString(nameNode, "langue", "lang", "language");
                if (!IsSameLanguage(declared, language))
                {
                    continue;
                }

                var value = LocalizedMetadataSanitizer.SanitizeField(
                    fieldName,
                    ReadString(nameNode, "text", "value", "nom", "name", "libelle", "label"),
                    language);
                if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(value);
                }
            }
        }

        return string.Join(", ", values);
    }

    private static JsonElement FirstExistingProperty(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(node, name, out var value))
            {
                return value;
            }
        }

        return default;
    }

    private static IEnumerable<JsonElement> EnumerateArrayOrSingle(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                yield return item;
            }
            yield break;
        }

        if (node.ValueKind != JsonValueKind.Undefined && node.ValueKind != JsonValueKind.Null)
        {
            yield return node;
        }
    }

    private static string ReadLocalizedObjectString(JsonElement game, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(game, propertyName, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.String)
            {
                return node.GetString() ?? string.Empty;
            }

            var value = ReadString(node, "text", "nom", "name", "value");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static JsonElement FirstExistingRom(JsonElement game)
    {
        if (TryGetProperty(game, "rom", out var rom) && rom.ValueKind == JsonValueKind.Object)
        {
            return rom;
        }

        if (TryGetProperty(game, "roms", out var roms) && roms.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in roms.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.Object)
                {
                    return candidate;
                }
            }
        }

        return default;
    }

    private static string ReadRegion(JsonElement game, JsonElement rom, string language)
    {
        var direct = ReadString(game, "region");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (TryGetProperty(rom, "regions", out var regions))
        {
            var languageRegion = ReadString(regions, $"regions_{NormalizeLanguage(language)}");
            if (!string.IsNullOrWhiteSpace(languageRegion))
            {
                return languageRegion;
            }

            var shortName = ReadString(regions, "regions_shortname", "shortname", "region");
            if (!string.IsNullOrWhiteSpace(shortName))
            {
                return shortName;
            }
        }

        return ReadNestedString(rom, "romregions", "region", "regionshortname");
    }

    private static string ReadRomLanguageField(JsonElement game, JsonElement rom)
    {
        var languages = ReadRomLanguageValues(rom)
            .Concat(ReadDirectGameLanguageValues(game))
            .SelectMany(SplitRomLanguageTokens)
            .Select(NormalizeRomLanguageToken)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return languages.Count == 0 ? string.Empty : string.Join(", ", languages);
    }

    private static IEnumerable<string> ReadDirectGameLanguageValues(JsonElement game)
    {
        foreach (var propertyName in new[] { "langues", "langues_shortname", "languages" })
        {
            if (TryGetProperty(game, propertyName, out var node))
            {
                foreach (var value in ReadRomLanguageValues(node))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<string> ReadRomLanguageValues(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            yield return node.GetString() ?? string.Empty;
            yield break;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var value in ReadRomLanguageValues(item))
                {
                    yield return value;
                }
            }

            yield break;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var propertyName in new[] { "langue", "lang", "language", "languages", "langues", "langues_shortname" })
        {
            if (!TryGetProperty(node, propertyName, out var child))
            {
                continue;
            }

            foreach (var value in ReadRomLanguageValues(child))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> SplitRomLanguageTokens(string value)
    {
        return (value ?? string.Empty)
            .Split([',', ';', '/', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeRomLanguageToken(string value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('_', '-');
        return normalized switch
        {
            "english" or "eng" or "en-us" or "en-gb" => "en",
            "french" or "fra" or "fre" or "fr-fr" => "fr",
            "german" or "deu" or "ger" or "de-de" => "de",
            "spanish" or "spa" or "es-es" or "sp" => "es",
            "italian" or "ita" or "it-it" => "it",
            "portuguese" or "por" or "pt-pt" or "pt-br" => "pt",
            "japanese" or "jpn" or "ja-jp" or "jp" => "ja",
            "dutch" or "nld" or "nl-nl" => "nl",
            "russian" or "rus" or "ru-ru" => "ru",
            "polish" or "pol" or "pl-pl" => "pl",
            "czech" or "ces" or "cze" or "cs-cz" => "cs",
            "turkish" or "tur" or "tr-tr" => "tr",
            "korean" or "kor" or "ko-kr" => "ko",
            "chinese" or "chi" or "zho" or "zh-cn" or "zh-tw" => "zh",
            _ => normalized.Length >= 2 ? normalized[..2] : string.Empty
        };
    }

    private static string ReadReleaseDate(JsonElement game)
    {
        if (!TryGetProperty(game, "dates", out var dates))
        {
            return ReadString(game, "date", "releasedate");
        }

        foreach (var date in EnumerateArrayOrSingle(dates))
        {
            var region = ReadString(date, "region");
            if (region.Equals("wor", StringComparison.OrdinalIgnoreCase) ||
                region.Equals("eu", StringComparison.OrdinalIgnoreCase) ||
                region.Equals("us", StringComparison.OrdinalIgnoreCase) ||
                region.Equals("ss", StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadString(date, "text", "value", "date");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        foreach (var date in EnumerateArrayOrSingle(dates))
        {
            var value = ReadString(date, "text", "value", "date");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadRating(JsonElement game)
    {
        var direct = ReadString(game, "score", "rating");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (TryGetProperty(game, "note", out var note))
        {
            return ReadElementString(note) is { Length: > 0 } value
                ? value
                : ReadString(note, "text", "value", "score", "rating");
        }

        return string.Empty;
    }

    private static string ReadNestedString(JsonElement node, params string[] pathOrNames)
    {
        if (pathOrNames.Length == 0)
        {
            return string.Empty;
        }

        if (TryGetProperty(node, pathOrNames[0], out var child))
        {
            if (pathOrNames.Length == 1)
            {
                return ReadElementString(child);
            }

            if (child.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in child.EnumerateArray())
                {
                    var value = ReadNestedString(item, pathOrNames[1..]);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            var nested = ReadNestedString(child, pathOrNames[1..]);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return ReadString(node, pathOrNames);
    }

    private static string ReadString(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(node, name, out var value))
            {
                var text = ReadElementString(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadElementString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => System.Net.WebUtility.HtmlDecode(value.GetString() ?? string.Empty).Trim(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Array => value.EnumerateArray().Select(ReadElementString).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static bool TryGetProperty(JsonElement node, string name, out JsonElement value)
    {
        value = default;
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in node.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeReleaseDate(string value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d{8}T\d{6}$"))
        {
            return raw;
        }

        if (System.Text.RegularExpressions.Regex.Match(raw, @"^(\d{4})-(\d{2})-(\d{2})") is { Success: true } dateMatch)
        {
            return $"{dateMatch.Groups[1].Value}{dateMatch.Groups[2].Value}{dateMatch.Groups[3].Value}T000000";
        }

        return System.Text.RegularExpressions.Regex.Match(raw, @"^(\d{4})$") is { Success: true } yearMatch
            ? $"{yearMatch.Groups[1].Value}0101T000000"
            : string.Empty;
    }

    private static string NormalizeRating(string value)
    {
        if (!double.TryParse(
                (value ?? string.Empty).Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rating))
        {
            return string.Empty;
        }

        if (rating > 1)
        {
            rating /= 20d;
        }

        return Math.Clamp(rating, 0d, 1d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeLanguage(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized.Length >= 2 ? normalized[..2] : string.Empty;
    }

    private static bool IsSameLanguage(string? left, string? right)
    {
        var normalizedLeft = NormalizeLanguage(left);
        var normalizedRight = NormalizeLanguage(right);
        return normalizedLeft.Length == 2 &&
            string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "_" : safe;
    }

    private static void AddError(ScreenScraperRawCacheMetadataRebuildSystemResult result, string error)
    {
        if (result.Errors.Count < 20)
        {
            result.Errors.Add(error);
        }
    }

    private sealed record ScreenScraperRawCachePayload(string GameSlug, string Path);
}

public sealed class ScreenScraperRawCacheMetadataRebuildResult
{
    public int SystemsProcessed { get; set; }
    public int PayloadsScanned { get; set; }
    public int PayloadsFailed { get; set; }
    public int MetadataBundlesWritten { get; set; }
    public int MetadataBundlesSkipped { get; set; }
    public List<ScreenScraperRawCacheMetadataRebuildSystemResult> SystemResults { get; } = new();
}

public sealed class ScreenScraperRawCacheMetadataRebuildSystemResult
{
    public string SystemId { get; set; } = string.Empty;
    public int PayloadsScanned { get; set; }
    public int PayloadsFailed { get; set; }
    public int MetadataBundlesWritten { get; set; }
    public int MetadataBundlesSkipped { get; set; }
    public List<string> Errors { get; } = new();
}
