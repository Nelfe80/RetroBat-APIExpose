using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public partial class LocalizedTextStore : ILocalizedTextStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<LocalizedTextRecord>> PersistAsync(string systemId, string gameSlug, GameDetails? details, CancellationToken cancellationToken = default)
    {
        var records = new List<LocalizedTextRecord>();
        if (details == null || string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(gameSlug))
        {
            return records;
        }

        foreach (var group in ExtractCandidates(details).GroupBy(candidate => LocalizedMetadataBundleNormalizer.NormalizeLanguage(candidate.Language), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in group)
            {
                var fieldName = LocalizedMetadataBundleNormalizer.NormalizeFieldName(candidate.Kind);
                var content = candidate.Content?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                if (!fields.TryGetValue(fieldName, out var existing) || existing.Trim().Length < content.Length)
                {
                    fields[fieldName] = content;
                }
            }

            if (fields.Count == 0)
            {
                continue;
            }

            var language = group.Key;
            var bundlePath = GetBundlePath(systemId, gameSlug, language);
            var wasWritten = await PersistFieldsAsync(systemId, gameSlug, language, fields, cancellationToken);
            records.Add(new LocalizedTextRecord
            {
                Language = language,
                Kind = "bundle",
                Path = bundlePath,
                WasWritten = wasWritten
            });
        }

        return records;
    }

    public async Task<bool> PersistFieldsAsync(string systemId, string gameSlug, string language, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(gameSlug) || fields.Count == 0)
        {
            return false;
        }

        var normalizedLanguage = LocalizedMetadataBundleNormalizer.NormalizeLanguage(language);
        var bundlePath = GetBundlePath(systemId, gameSlug, normalizedLanguage);
        var (bundle, normalizedOnLoad) = await LoadExistingBundleForWriteAsync(
            systemId,
            gameSlug,
            normalizedLanguage,
            bundlePath,
            cancellationToken);
        var updated = normalizedOnLoad;

        foreach (var entry in fields)
        {
            var fieldName = LocalizedMetadataBundleNormalizer.NormalizeFieldName(entry.Key);
            var content = LocalizedMetadataSanitizer.SanitizeField(fieldName, entry.Value, normalizedLanguage);
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                if (IsExplicitlyClearableField(fieldName) && bundle.Fields.Remove(fieldName))
                {
                    updated = true;
                }

                continue;
            }

            if (ShouldReplaceField(bundle.Fields, fieldName, content, normalizedLanguage, fields))
            {
                bundle.Fields[fieldName] = content;
                updated = true;
            }
        }

        if (!updated)
        {
            return false;
        }

        bundle.UpdatedAtUtc = DateTime.UtcNow;
        var directory = Path.GetDirectoryName(bundlePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(bundlePath);
        await JsonSerializer.SerializeAsync(stream, bundle, JsonOptions, cancellationToken);
        return true;
    }

    private static bool IsExplicitlyClearableField(string fieldName)
    {
        return string.Equals(fieldName, "lang", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<LocalizedTextBundle?> LoadPreferredBundleAsync(
        string systemId,
        string gameSlug,
        string requestedLanguage,
        CancellationToken cancellationToken = default,
        bool allowAnyLanguageFallback = true,
        bool allowEnglishFallback = true)
    {
        var candidates = new List<(string Path, string Language)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetCanonicalRootCandidates())
        {
            var textRoot = Path.Combine(root, systemId, "games", gameSlug, "texts");
            if (!Directory.Exists(textRoot))
            {
                continue;
            }

            foreach (var language in BuildLanguageFallbackOrder(requestedLanguage, allowEnglishFallback))
            {
                TryAddBundleCandidate(
                    candidates,
                    seenPaths,
                    Path.Combine(textRoot, $"metadata-{language}.json"),
                    language);
            }

            if (!allowAnyLanguageFallback)
            {
                continue;
            }

            foreach (var anyBundlePath in EnumerateAnyBundleCandidates(textRoot))
            {
                if (!seenPaths.Add(anyBundlePath))
                {
                    continue;
                }

                var language = ResolveBundleLanguage(anyBundlePath);
                candidates.Add((anyBundlePath, language));
            }
        }

        LocalizedTextBundle? merged = null;
        foreach (var candidate in candidates)
        {
            var bundle = await LoadBundleAsync(candidate.Path, candidate.Language, cancellationToken);
            if (bundle.Fields.Count == 0)
            {
                continue;
            }

            if (merged == null)
            {
                merged = new LocalizedTextBundle
                {
                    Language = bundle.Language,
                    UpdatedAtUtc = bundle.UpdatedAtUtc,
                    Fields = new Dictionary<string, string>(bundle.Fields, StringComparer.OrdinalIgnoreCase)
                };
                continue;
            }

            foreach (var field in bundle.Fields)
            {
                if (!merged.Fields.TryGetValue(field.Key, out var existing) ||
                    string.IsNullOrWhiteSpace(existing) ||
                    IsPlaceholderValue(field.Key, existing))
                {
                    merged.Fields[field.Key] = field.Value;
                }
            }

            if (bundle.UpdatedAtUtc > merged.UpdatedAtUtc)
            {
                merged.UpdatedAtUtc = bundle.UpdatedAtUtc;
            }
        }

        return merged;
    }

    private static IReadOnlyList<LocalizedTextCandidate> ExtractCandidates(GameDetails details)
    {
        var results = new List<LocalizedTextCandidate>();
        var defaultLanguage = LocalizedMetadataBundleNormalizer.NormalizeLanguage(details.Lang);

        AddCandidate(results, ResolveLikelyTextLanguage(defaultLanguage, details.Desc, "desc"), "desc", details.Desc);
        AddCandidate(results, defaultLanguage, "developer", details.Developer);
        AddCandidate(results, defaultLanguage, "publisher", details.Publisher);
        AddCandidate(results, ResolveLikelyTextLanguage(defaultLanguage, details.Genre, "genre"), "genre", details.Genre);
        AddCandidate(results, ResolveLikelyTextLanguage(defaultLanguage, details.Family, "family"), "family", details.Family);
        AddCandidate(results, ResolveLikelyTextLanguage(defaultLanguage, details.Genres, "genres"), "genres", details.Genres);
        AddCandidate(results, defaultLanguage, "players", details.Players);
        AddCandidate(results, defaultLanguage, "lang", details.Lang);
        AddCandidate(results, defaultLanguage, "region", details.Region);
        AddCandidate(results, defaultLanguage, "releasedate", details.Releasedate);
        AddCandidate(results, defaultLanguage, "rating", details.Rating);
        AddCandidate(results, defaultLanguage, "md5", details.Md5);
        AddCandidate(results, defaultLanguage, "name", details.Name);
        AddCandidate(results, defaultLanguage, "system", details.SystemName);
        AddCandidate(results, defaultLanguage, "emulator", details.Emulator);
        AddCandidate(results, defaultLanguage, "source", details.ScrapName);

        foreach (var entry in details.Extras)
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            var match = TextKeyRegex().Match(entry.Key);
            if (match.Success)
            {
                var kind = NormalizeTextKind(match.Groups["kind"].Value);
                if (string.IsNullOrWhiteSpace(kind))
                {
                    continue;
                }

                var language = match.Groups["lang"].Success
                    ? LocalizedMetadataBundleNormalizer.NormalizeLanguage(match.Groups["lang"].Value)
                    : defaultLanguage;

                AddCandidate(results, language, kind, entry.Value);
                continue;
            }

            var inferredLanguage = ResolveLikelyTextLanguage(defaultLanguage, entry.Value, entry.Key);
            AddCandidate(results, inferredLanguage, entry.Key.Trim().ToLowerInvariant(), entry.Value);
        }

        return results;
    }

    private static string NormalizeTextKind(string rawKind)
    {
        var normalized = rawKind.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "desc" => "desc",
            "description" => "desc",
            "story" => "story",
            "instructions" => "instructions",
            "instructionstext" => "instructions",
            "instructions-text" => "instructions",
            "trivia" => "trivia",
            "history" => "history",
            "notes" => "notes",
            _ => string.Empty
        };
    }

    private static bool ShouldReplaceField(
        IDictionary<string, string> fields,
        string fieldName,
        string incomingValue,
        string bundleLanguage,
        IReadOnlyDictionary<string, string> incomingFields)
    {
        if (!fields.TryGetValue(fieldName, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            return true;
        }

        var normalizedExisting = existing.Trim();
        var normalizedIncoming = incomingValue.Trim();

        if (IsPlaceholderValue(fieldName, normalizedExisting) && !IsPlaceholderValue(fieldName, normalizedIncoming))
        {
            return true;
        }

        if (string.Equals(fieldName, "releasedate", StringComparison.OrdinalIgnoreCase))
        {
            if (IsEsReleaseDate(normalizedIncoming) && !IsEsReleaseDate(normalizedExisting))
            {
                return true;
            }

            if (IsEsReleaseDate(normalizedIncoming) &&
                IsEsReleaseDate(normalizedExisting) &&
                !string.Equals(normalizedExisting, normalizedIncoming, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (string.Equals(fieldName, "rating", StringComparison.OrdinalIgnoreCase))
        {
            return ShouldReplaceRating(normalizedExisting, normalizedIncoming);
        }

        if (string.Equals(fieldName, "desc", StringComparison.OrdinalIgnoreCase))
        {
            var existingSource = fields.TryGetValue("source", out var rawExistingSource) ? rawExistingSource : string.Empty;
            var incomingSource = incomingFields.TryGetValue("source", out var rawIncomingSource) ? rawIncomingSource : string.Empty;
            var existingIsTranslated = IsTranslatedDescriptionSource(existingSource);
            var incomingIsTranslated = IsTranslatedDescriptionSource(incomingSource);
            if (existingIsTranslated && !incomingIsTranslated)
            {
                return true;
            }

            if (!existingIsTranslated && incomingIsTranslated &&
                !IsLikelyWrongLanguageForBundle(fieldName, normalizedExisting, fields, normalizedIncoming, bundleLanguage))
            {
                return false;
            }
        }

        if (IsLocalizedHumanTextField(fieldName) &&
            IsLikelyWrongLanguageForBundle(fieldName, normalizedExisting, fields, normalizedIncoming, bundleLanguage))
        {
            return true;
        }

        if (string.Equals(fieldName, "lang", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                LocalizedMetadataBundleNormalizer.NormalizeLanguage(normalizedExisting),
                LocalizedMetadataBundleNormalizer.NormalizeLanguage(normalizedIncoming),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedExisting.Length < normalizedIncoming.Length;
    }

    private static bool IsTranslatedDescriptionSource(string? source)
    {
        var value = source ?? string.Empty;
        return value.Contains("translateLocally:desc:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("argos:desc:", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLikelyTextLanguage(string declaredLanguage, string? text, string fieldName)
    {
        var normalized = LocalizedMetadataBundleNormalizer.NormalizeLanguage(declaredLanguage);
        var detected = DetectSupportedTextLanguage(text, fieldName);
        if (string.IsNullOrWhiteSpace(detected))
        {
            return normalized;
        }

        if (string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(detected, "fr", StringComparison.OrdinalIgnoreCase))
        {
            return "fr";
        }

        if (string.Equals(normalized, "fr", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(detected, "en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        return normalized;
    }

    private static bool IsLikelyWrongLanguageForBundle(string fieldName, string existing, IDictionary<string, string> fields, string incoming, string fallbackBundleLanguage)
    {
        var rawLanguage = fields.TryGetValue("lang", out var existingLanguageField)
            ? existingLanguageField
            : fallbackBundleLanguage;
        var bundleLanguage = LocalizedMetadataBundleNormalizer.NormalizeLanguage(rawLanguage);
        var existingLanguage = DetectSupportedTextLanguage(existing, fieldName);
        var incomingLanguage = DetectSupportedTextLanguage(incoming, fieldName);
        return !string.IsNullOrWhiteSpace(bundleLanguage) &&
            !string.IsNullOrWhiteSpace(existingLanguage) &&
            !string.Equals(existingLanguage, bundleLanguage, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(incomingLanguage) ||
                string.Equals(incomingLanguage, bundleLanguage, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalizedHumanTextField(string fieldName)
    {
        return fieldName.Equals("desc", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("family", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("genres", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectSupportedTextLanguage(string? text, string fieldName)
    {
        var value = (text ?? string.Empty).Trim();
        if (IsLocalizedHumanTextField(fieldName))
        {
            var labelLanguage = DetectShortLocalizedLabelLanguage(value);
            if (!string.IsNullOrWhiteSpace(labelLanguage))
            {
                return labelLanguage;
            }
        }

        if (value.Length < 24)
        {
            return string.Empty;
        }

        var lower = value.ToLowerInvariant();
        var frenchScore = CountMatches(lower, " le ", " la ", " les ", " des ", " une ", " un ", " vous ", " joueur", " jeu ", " dans ", " avec ", " pour ", " est ", " sont ", " qui ", " que ", " à ", " é", "è", "ç", "ù", "ê", "û", "î", "ô");
        var englishScore = CountMatches(lower, " the ", " and ", " you ", " your ", " player", " game ", " with ", " for ", " is ", " are ", " in ", " on ", " to ", " of ", " from ", " this ", " that ");

        if (frenchScore >= englishScore + 2 && frenchScore >= 3)
        {
            return "fr";
        }

        if (englishScore >= frenchScore + 2 && englishScore >= 3)
        {
            return "en";
        }

        return string.Empty;
    }

    private static string DetectShortLocalizedLabelLanguage(string text)
    {
        var tokens = text
            .ToLowerInvariant()
            .Replace("\\", "/", StringComparison.Ordinal)
            .Split([',', '/', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var frenchScore = tokens.Count(IsLikelyFrenchLocalizedLabel);
        var englishScore = tokens.Count(IsLikelyEnglishLocalizedLabel);
        if (frenchScore > 0 && frenchScore > englishScore)
        {
            return "fr";
        }

        if (englishScore > 0 && englishScore > frenchScore)
        {
            return "en";
        }

        return string.Empty;
    }

    private static bool IsLikelyFrenchLocalizedLabel(string token)
    {
        return token is "aventure" or "plateforme" or "tir" or "course" or "conduite" or
            "jeu de roles" or "jeu de role" or "jeu de r\u00f4les" or "jeu de r\u00f4le" or
            "jeu de societe" or "jeu de soci\u00e9t\u00e9" or "reflexion" or "r\u00e9flexion" or
            "labyrinthe" or "avion" or "gestion" or "beat'em all" or "divers" or "flipper" or
            "tir avec accessoire" or "sport" or "boxe" or "multisports" or
            "combat";
    }

    private static bool IsLikelyEnglishLocalizedLabel(string token)
    {
        return token is "adventure" or "platform" or "platformer" or "shooter" or "shooting" or
            "racing" or "driving" or "board game" or "board games" or "role playing game" or
            "role playing games" or "rpg" or "puzzle" or "maze" or "flight" or "management" or
            "miscellaneous" or "pinball" or "lightgun shooter" or "sports" or "boxing" or
            "multisport" or "fighting";
    }

    private static int CountMatches(string value, params string[] needles)
    {
        var count = 0;
        foreach (var needle in needles)
        {
            var index = -needle.Length;
            while ((index = value.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsPlaceholderValue(string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (string.Equals(fieldName, "releasedate", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value, "not-a-date-time", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldReplaceRating(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return false;
        }

        if (!TryParseRating(incoming, out var incomingRating))
        {
            return false;
        }

        if (!TryParseRating(existing, out var existingRating))
        {
            return true;
        }

        if (existingRating <= 0 && incomingRating > 0)
        {
            return true;
        }

        return incomingRating > existingRating;
    }

    private static bool TryParseRating(string value, out double rating)
    {
        if (!double.TryParse(
            (value ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out rating))
        {
            return false;
        }

        if (rating > 1)
        {
            rating /= 20.0;
        }

        rating = Math.Clamp(rating, 0, 1);
        return true;
    }

    private static bool IsEsReleaseDate(string value)
    {
        return Regex.IsMatch(value, @"^\d{8}T\d{6}$");
    }

    private static string GetBundlePath(string systemId, string gameSlug, string language)
    {
        return Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            systemId,
            "games",
            gameSlug,
            "texts",
            $"metadata-{language}.json");
    }

    private static async Task<(LocalizedTextBundle Bundle, bool Normalized)> LoadExistingBundleForWriteAsync(
        string systemId,
        string gameSlug,
        string language,
        string bundlePath,
        CancellationToken cancellationToken)
    {
        _ = systemId;
        _ = gameSlug;
        return await LoadBundleWithNormalizationTraceAsync(bundlePath, language, cancellationToken);
    }

    private static async Task<LocalizedTextBundle> LoadBundleAsync(string path, string language, CancellationToken cancellationToken)
    {
        var (bundle, _) = await LoadBundleWithNormalizationTraceAsync(path, language, cancellationToken);
        return bundle;
    }

    private static async Task<(LocalizedTextBundle Bundle, bool Normalized)> LoadBundleWithNormalizationTraceAsync(string path, string language, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (new LocalizedTextBundle { Language = language }, false);
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0)
        {
            return (new LocalizedTextBundle { Language = language }, false);
        }

        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var bundle = await JsonSerializer.DeserializeAsync<LocalizedTextBundle>(stream, JsonOptions, cancellationToken);
            bundle ??= new LocalizedTextBundle { Language = language };
            var normalized = LocalizedMetadataBundleNormalizer.NormalizeBundleFields(bundle, language);
            return (bundle, normalized);
        }
        catch (JsonException)
        {
            return (new LocalizedTextBundle { Language = language }, false);
        }
    }

    private static LocalizedTextBundle NormalizeBundleFields(LocalizedTextBundle bundle, string language)
    {
        LocalizedMetadataBundleNormalizer.NormalizeBundleFields(bundle, language);
        return bundle;
    }

    private static IEnumerable<string> GetCanonicalRootCandidates()
    {
        yield return RetroBatPaths.MediaUserSystemsRoot;
        yield return RetroBatPaths.MediaSystemsRoot;
    }

    private static void TryAddBundleCandidate(
        ICollection<(string Path, string Language)> candidates,
        ISet<string> seenPaths,
        string path,
        string language)
    {
        if (File.Exists(path) && seenPaths.Add(path))
        {
            candidates.Add((path, language));
        }
    }

    private static IEnumerable<string> EnumerateAnyBundleCandidates(string textRoot)
    {
        foreach (var flatPath in Directory.GetFiles(textRoot, "metadata-*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return flatPath;
        }

    }

    private static string ResolveBundleLanguage(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.StartsWith("metadata-", StringComparison.OrdinalIgnoreCase) &&
            fileName.Length > "metadata-".Length)
        {
            return LocalizedMetadataBundleNormalizer.NormalizeLanguage(fileName["metadata-".Length..]);
        }
        return "en";
    }

    private static IEnumerable<string> BuildLanguageFallbackOrder(string requestedLanguage, bool allowEnglishFallback = true)
    {
        var normalized = LocalizedMetadataBundleNormalizer.NormalizeLanguage(requestedLanguage);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        if (allowEnglishFallback && !string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase))
        {
            yield return "en";
        }
    }

    private static void AddCandidate(ICollection<LocalizedTextCandidate> results, string language, string kind, string? content)
    {
        var normalized = content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        results.Add(new LocalizedTextCandidate
        {
            Language = language,
            Kind = kind,
            Content = normalized,
            SourceKey = kind
        });
    }

    [GeneratedRegex(@"^(?<kind>desc|description|story|instructions|instructions_text|instructionstext|trivia|history|notes)(?:[_-](?<lang>[a-z]{2}))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TextKeyRegex();
}
