using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class LocalizedGamelistCacheService
{
    private const string ManifestFileName = "manifest.json";
    private const string CacheLogFileName = "localized-gamelist-cache.jsonl";
    private const int CacheSchemaVersion = 4;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim LogGate = new(1, 1);
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "da", "de", "en", "es", "fi", "fr", "it", "ja", "ko", "nl", "pl", "pt", "ru", "sv", "tr", "zh"
    };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILocalizedTextStore _localizedTextStore;
    private readonly IMediaAliasStore _mediaAliasStore;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly ILogger<LocalizedGamelistCacheService>? _logger;

    public LocalizedGamelistCacheService(
        IOptionsMonitor<ApiExposeOptions> options,
        ILocalizedTextStore localizedTextStore,
        IMediaAliasStore mediaAliasStore,
        GameNameNormalizer gameNameNormalizer,
        SystemIdNormalizer systemIdNormalizer,
        GamelistUpdateService gamelistUpdateService,
        ILogger<LocalizedGamelistCacheService>? logger = null)
    {
        _options = options;
        _localizedTextStore = localizedTextStore;
        _mediaAliasStore = mediaAliasStore;
        _gameNameNormalizer = gameNameNormalizer;
        _systemIdNormalizer = systemIdNormalizer;
        _gamelistUpdateService = gamelistUpdateService;
        _logger = logger;
    }

    public bool Enabled => _options.CurrentValue.LocalizedGamelistCache.Enabled;

    public IReadOnlyList<string> ResolveActiveLanguages(string? includeLanguage = null)
    {
        var result = new List<string>();
        AddLanguage(result, ResolveWindowsLanguage());
        AddLanguage(result, "en");

        foreach (var language in _options.CurrentValue.LocalizedGamelistCache.ActiveLanguages)
        {
            AddLanguage(result, language);
        }

        AddLanguage(result, includeLanguage);
        return result;
    }

    public async Task<LocalizedGamelistSwitchResult> SwitchToLanguageAsync(
        string language,
        CancellationToken cancellationToken = default)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        if (string.IsNullOrWhiteSpace(normalizedLanguage))
        {
            normalizedLanguage = ResolveWindowsLanguage();
        }

        if (!Enabled)
        {
            return new LocalizedGamelistSwitchResult(normalizedLanguage, 0, 0, 0, false, "disabled");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var systems = EnumerateSystemsWithGamelist().ToList();
            var generated = 0;
            var failed = 0;
            var staleGenerationFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var systemId in systems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSystemCacheFresh(normalizedLanguage, systemId))
                {
                    try
                    {
                        await GenerateSystemCacheAsync(normalizedLanguage, systemId, cancellationToken);
                        generated++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        staleGenerationFailures.Add(systemId);
                        _logger?.LogWarning(
                            ex,
                            "Echec de generation du cache gamelist localise: language={Language}, system={System}.",
                            normalizedLanguage,
                            systemId);
                        await TryAppendCacheLogAsync(
                            "switch",
                            "generate-system-failed",
                            new { language = normalizedLanguage, systemId, exceptionType = ex.GetType().FullName, ex.Message },
                            CancellationToken.None);
                    }
                }
            }

            var switched = 0;
            foreach (var systemId in systems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (staleGenerationFailures.Contains(systemId))
                {
                    continue;
                }

                try
                {
                    if (SwitchSystemCache(normalizedLanguage, systemId, cancellationToken))
                    {
                        switched++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger?.LogWarning(
                        ex,
                        "Echec du switch du cache gamelist localise: language={Language}, system={System}.",
                        normalizedLanguage,
                        systemId);
                    await TryAppendCacheLogAsync(
                        "switch",
                        "switch-system-failed",
                        new { language = normalizedLanguage, systemId, exceptionType = ex.GetType().FullName, ex.Message },
                        CancellationToken.None);
                }
            }

            await WriteManifestAsync(normalizedLanguage, systems, cancellationToken);
            _logger?.LogInformation(
                "Localized gamelist cache switched: language={Language}, systems={Systems}, generated={Generated}, failed={Failed}.",
                normalizedLanguage,
                switched,
                generated,
                failed);
            return new LocalizedGamelistSwitchResult(
                normalizedLanguage,
                switched,
                generated,
                failed,
                switched > 0 || systems.Count == 0,
                failed == 0 ? "switched" : "switched-partial");
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<LocalizedGamelistPrebuildResult> PrebuildActiveLanguagesAsync(
        IReadOnlyCollection<string>? systemIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return new LocalizedGamelistPrebuildResult(Array.Empty<string>(), 0, 0, "disabled");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var systems = ResolveSystemsToBuild(systemIds);
            var languages = ResolveActiveLanguages();
            var generated = 0;
            var failed = 0;
            await TryAppendCacheLogAsync(
                "prebuild",
                "started",
                new { languages, systems = systems.Count },
                cancellationToken);

            foreach (var language in languages)
            {
                foreach (var systemId in systems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await GenerateSystemCacheAsync(language, systemId, cancellationToken);
                        generated++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger?.LogWarning(
                            ex,
                            "Echec de generation du cache gamelist localise: language={Language}, system={System}.",
                            language,
                            systemId);
                        await TryAppendCacheLogAsync(
                            "prebuild",
                            "generate-system-failed",
                            new { language, systemId, exceptionType = ex.GetType().FullName, ex.Message },
                            CancellationToken.None);
                    }
                }

                await WriteManifestAsync(language, systems, cancellationToken);
            }

            _logger?.LogInformation(
                "Localized gamelist cache prebuilt: languages={Languages}, systems={Systems}, generated={Generated}, failed={Failed}.",
                string.Join(",", languages),
                systems.Count,
                generated,
                failed);
            await TryAppendCacheLogAsync(
                "prebuild",
                failed == 0 ? "completed" : "completed-partial",
                new { languages, systems = systems.Count, generated, failed },
                cancellationToken);
            return new LocalizedGamelistPrebuildResult(
                languages,
                generated,
                failed,
                failed == 0 ? "prebuilt" : "prebuilt-partial");
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<LocalizedGamelistPrebuildResult> PrebuildActiveLanguagesAsync(CancellationToken cancellationToken = default)
    {
        return PrebuildActiveLanguagesAsync(systemIds: null, cancellationToken);
    }

    public async Task<LocalizedGamelistEntryPatchResult> PatchActiveLanguageEntriesAsync(
        IReadOnlyCollection<LocalizedGamelistCacheRefreshEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return new LocalizedGamelistEntryPatchResult(Array.Empty<string>(), 0, 0, 0, 0, 0, "disabled");
        }

        var requestedEntries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FrontendSystemId))
            .GroupBy(BuildEntryRefreshKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(entry => entry.FrontendSystemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GamePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GameSlug, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requestedEntries.Count == 0)
        {
            return new LocalizedGamelistEntryPatchResult(Array.Empty<string>(), 0, 0, 0, 0, 0, "empty");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var languages = ResolveActiveLanguages();
            var entriesPatched = 0;
            var entriesSkipped = 0;
            var filesSaved = 0;
            var filesFailed = 0;
            await TryAppendCacheLogAsync(
                "entry-patch",
                "started",
                new { languages, entries = requestedEntries.Count },
                cancellationToken);

            foreach (var language in languages)
            {
                foreach (var systemGroup in requestedEntries.GroupBy(entry => entry.FrontendSystemId, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var systemEntries = systemGroup.ToList();
                    try
                    {
                        var result = await PatchSystemCacheEntriesAsync(
                            language,
                            systemGroup.Key,
                            systemEntries,
                            cancellationToken);
                        entriesPatched += result.EntriesPatched;
                        entriesSkipped += result.EntriesSkipped;
                        if (result.FileSaved)
                        {
                            filesSaved++;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        filesFailed++;
                        entriesSkipped += systemEntries.Count;
                        _logger?.LogWarning(
                            ex,
                            "Echec du patch cache gamelist localise: language={Language}, system={System}.",
                            language,
                            systemGroup.Key);
                        await TryAppendCacheLogAsync(
                            "entry-patch",
                            "system-failed",
                            new { language, systemId = systemGroup.Key, entries = systemEntries.Count, exceptionType = ex.GetType().FullName, ex.Message },
                            CancellationToken.None);
                    }
                }
            }

            await TryAppendCacheLogAsync(
                "entry-patch",
                filesFailed == 0 ? "completed" : "completed-partial",
                new { languages, requestedEntries = requestedEntries.Count, entriesPatched, entriesSkipped, filesSaved, filesFailed },
                cancellationToken);
            return new LocalizedGamelistEntryPatchResult(
                languages,
                requestedEntries.Count,
                entriesPatched,
                entriesSkipped,
                filesSaved,
                filesFailed,
                filesFailed == 0 ? "patched" : "patched-partial");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task GenerateSystemCacheAsync(string language, string frontendSystemId, CancellationToken cancellationToken)
    {
        var sourcePath = ResolveLiveGamelistPath(frontendSystemId);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var document = LoadGamelistDocument(sourcePath);
        var root = document.Root;
        if (root == null)
        {
            return;
        }

        var canonicalSystemId = _systemIdNormalizer.Normalize(frontendSystemId);
        var cache = new Dictionary<string, LocalizedTextBundle?>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameNode in root.Elements("game"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawPath = gameNode.Element("path")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var gameName = gameNode.Element("name")?.Value?.Trim() ?? string.Empty;
            var fallbackSlug = _gameNameNormalizer.NormalizeGameSlug(gameName, rawPath);
            var canonicalSlug = await ResolveCanonicalSlugAsync(canonicalSystemId, rawPath, gameName, fallbackSlug, cancellationToken);
            var familySlug = _gameNameNormalizer.NormalizeGameSlug(null, Path.GetFileNameWithoutExtension(rawPath));
            var bundle = await LoadBestTextBundleAsync(canonicalSystemId, canonicalSlug, familySlug, language, cache, cancellationToken);
            ApplyBundleText(gameNode, bundle, language);
        }

        var targetPath = ResolveCachedGamelistPath(language, frontendSystemId);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        SaveGamelistDocument(document, targetPath);
    }

    private async Task<LocalizedGamelistEntryPatchSystemResult> PatchSystemCacheEntriesAsync(
        string language,
        string frontendSystemId,
        IReadOnlyList<LocalizedGamelistCacheRefreshEntry> entries,
        CancellationToken cancellationToken)
    {
        var cachePath = ResolveCachedGamelistPath(language, frontendSystemId);
        if (!File.Exists(cachePath))
        {
            return new LocalizedGamelistEntryPatchSystemResult(0, entries.Count, false);
        }

        var livePath = ResolveLiveGamelistPath(frontendSystemId);
        if (!File.Exists(livePath))
        {
            return new LocalizedGamelistEntryPatchSystemResult(0, entries.Count, false);
        }

        var cacheDocument = LoadGamelistDocument(cachePath);
        var liveDocument = LoadGamelistDocument(livePath);
        var cacheRoot = cacheDocument.Root;
        var liveRoot = liveDocument.Root;
        if (cacheRoot == null || liveRoot == null)
        {
            return new LocalizedGamelistEntryPatchSystemResult(0, entries.Count, false);
        }

        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var canonicalSystemId = _systemIdNormalizer.Normalize(frontendSystemId);
        var bundleCache = new Dictionary<string, LocalizedTextBundle?>(StringComparer.OrdinalIgnoreCase);
        var entriesPatched = 0;
        var entriesSkipped = 0;
        var changed = false;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveNode = FindMatchingGameNode(liveRoot, entry, systemRoot);
            if (liveNode == null)
            {
                entriesSkipped++;
                continue;
            }

            var refreshedNode = new XElement(liveNode);
            var rawPath = refreshedNode.Element("path")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                rawPath = ToGameRelativePath(entry.GamePath, systemRoot);
                SetOrCreateElement(refreshedNode, "path", rawPath);
            }

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                entriesSkipped++;
                continue;
            }

            var gameName = refreshedNode.Element("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(gameName))
            {
                gameName = entry.GameName;
            }

            var fallbackSlug = !string.IsNullOrWhiteSpace(entry.GameSlug)
                ? entry.GameSlug
                : _gameNameNormalizer.NormalizeGameSlug(gameName, rawPath);
            var canonicalSlug = await ResolveCanonicalSlugAsync(
                canonicalSystemId,
                rawPath,
                gameName ?? string.Empty,
                fallbackSlug,
                cancellationToken);
            var familySlug = _gameNameNormalizer.NormalizeGameSlug(null, Path.GetFileNameWithoutExtension(rawPath));
            var bundle = await LoadBestTextBundleAsync(
                canonicalSystemId,
                canonicalSlug,
                familySlug,
                language,
                bundleCache,
                cancellationToken);
            ApplyBundleText(refreshedNode, bundle, language);

            var cacheNode = FindMatchingGameNode(cacheRoot, entry, systemRoot) ??
                FindGameNodeByPath(cacheRoot, rawPath);
            var refreshedXml = refreshedNode.ToString(SaveOptions.DisableFormatting);
            if (cacheNode == null)
            {
                cacheRoot.Add(refreshedNode);
                changed = true;
            }
            else if (!string.Equals(cacheNode.ToString(SaveOptions.DisableFormatting), refreshedXml, StringComparison.Ordinal))
            {
                cacheNode.ReplaceWith(refreshedNode);
                changed = true;
            }

            entriesPatched++;
        }

        if (!changed)
        {
            return new LocalizedGamelistEntryPatchSystemResult(entriesPatched, entriesSkipped, false);
        }

        var saved = _gamelistUpdateService.SaveExternalGamelistDocument(
            cacheDocument,
            cachePath,
            "localized-gamelist-cache-entry-patch:" + NormalizeLanguage(language),
            cancellationToken,
            allowMediaTagDrop: true);
        return new LocalizedGamelistEntryPatchSystemResult(entriesPatched, entriesSkipped, saved);
    }

    private async Task<LocalizedTextBundle?> LoadBestTextBundleAsync(
        string systemId,
        string canonicalSlug,
        string familySlug,
        string language,
        IDictionary<string, LocalizedTextBundle?> cache,
        CancellationToken cancellationToken)
    {
        foreach (var slug in EnumerateSlugCandidates(canonicalSlug, familySlug))
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            if (cache.TryGetValue(slug, out var cached))
            {
                if (cached != null)
                {
                    return cached;
                }

                continue;
            }

            var bundle = await _localizedTextStore.LoadPreferredBundleAsync(
                systemId,
                slug,
                language,
                cancellationToken,
                allowAnyLanguageFallback: false);
            cache[slug] = bundle;
            if (bundle != null)
            {
                return bundle;
            }
        }

        return null;
    }

    private async Task<string> ResolveCanonicalSlugAsync(
        string systemId,
        string gamePath,
        string gameName,
        string fallbackSlug,
        CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            "path:" + NormalizePathKey(gamePath),
            "file:" + Path.GetFileName(gamePath),
            "slug:" + fallbackSlug,
            "name:" + _gameNameNormalizer.NormalizeGameSlug(gameName, gamePath)
        };
        return await _mediaAliasStore.ResolveGameSlugAsync(systemId, keys, fallbackSlug, cancellationToken);
    }

    private static void ApplyBundleText(XElement gameNode, LocalizedTextBundle? bundle, string language)
    {
        if (bundle == null || bundle.Fields.Count == 0)
        {
            NormalizeOrRemoveLocalizedFallbackElements(gameNode, language);
            return;
        }

        foreach (var fieldName in LocalizedTextFieldNames())
        {
            if (!bundle.Fields.TryGetValue(fieldName, out var value) || string.IsNullOrWhiteSpace(value))
            {
                if (ShouldClearMissingRomLanguage(bundle, fieldName))
                {
                    gameNode.Element(fieldName)?.Remove();
                }

                NormalizeOrRemoveLocalizedFallbackElement(gameNode, fieldName, language);
                continue;
            }

            SetOrCreateElement(
                gameNode,
                fieldName,
                LocalizedMetadataSanitizer.SanitizeField(fieldName, value, language));
        }
    }

    private static bool ShouldClearMissingRomLanguage(LocalizedTextBundle bundle, string fieldName)
    {
        return string.Equals(fieldName, "lang", StringComparison.OrdinalIgnoreCase) &&
            !bundle.Fields.ContainsKey("lang") &&
            bundle.Fields.TryGetValue("source", out var source) &&
            source.Contains("screenscraper", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeOrRemoveLocalizedFallbackElements(XElement gameNode, string language)
    {
        foreach (var fieldName in LocalizedFallbackFieldNames())
        {
            NormalizeOrRemoveLocalizedFallbackElement(gameNode, fieldName, language);
        }
    }

    private static void NormalizeOrRemoveLocalizedFallbackElement(XElement gameNode, string fieldName, string language)
    {
        if (!IsLocalizedFallbackField(fieldName))
        {
            return;
        }

        var element = gameNode.Element(fieldName);
        if (element == null || string.IsNullOrWhiteSpace(element.Value))
        {
            return;
        }

        var targetLanguage = NormalizeLanguage(language);
        if (!ShouldCleanLocalizedFallback(targetLanguage, fieldName, element.Value))
        {
            return;
        }

        var normalized = LocalizedMetadataSanitizer.SanitizeField(fieldName, element.Value, targetLanguage);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !IsLikelyWrongLanguageForTarget(fieldName, normalized, targetLanguage))
        {
            element.Value = normalized;
            return;
        }

        element.Remove();
    }

    private static IEnumerable<string> LocalizedFallbackFieldNames()
    {
        yield return "desc";
        yield return "genre";
        yield return "family";
    }

    private static bool IsLocalizedFallbackField(string fieldName)
    {
        return fieldName.Equals("desc", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("family", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCleanLocalizedFallback(string targetLanguage, string fieldName, string value)
    {
        return IsLocalizedFallbackField(fieldName) &&
            (string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetLanguage, "fr", StringComparison.OrdinalIgnoreCase)) &&
            IsLikelyWrongLanguageForTarget(fieldName, value, targetLanguage);
    }

    private static bool IsLikelyWrongLanguageForTarget(string fieldName, string value, string targetLanguage)
    {
        var detected = DetectSupportedTextLanguage(fieldName, value);
        return !string.IsNullOrWhiteSpace(detected) &&
            !string.IsNullOrWhiteSpace(targetLanguage) &&
            !string.Equals(detected, targetLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectSupportedTextLanguage(string fieldName, string? text)
    {
        var value = LocalizedMetadataSanitizer.SanitizeText(text);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (fieldName.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("family", StringComparison.OrdinalIgnoreCase))
        {
            return DetectShortLocalizedLabelLanguage(value);
        }

        if (value.Length < 24)
        {
            return string.Empty;
        }

        var padded = " " + value.ToLowerInvariant() + " ";
        var frenchScore = CountMatches(
            padded,
            " le ", " la ", " les ", " des ", " une ", " un ", " vous ", " joueur", " jeu ",
            " dans ", " avec ", " pour ", " est ", " sont ", " qui ", " que ",
            " \u00e0 ", "\u00e9", "\u00e8", "\u00e7", "\u00f9", "\u00ea", "\u00fb", "\u00ee", "\u00f4");
        var englishScore = CountMatches(
            padded,
            " the ", " and ", " you ", " your ", " player", " game ", " with ", " for ",
            " is ", " are ", " in ", " on ", " to ", " of ", " from ", " this ", " that ");

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
            "tir avec accessoire" or "sport" or "boxe" or "multisports" or "combat";
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

    private bool SwitchSystemCache(string language, string frontendSystemId, CancellationToken cancellationToken)
    {
        var cachePath = ResolveCachedGamelistPath(language, frontendSystemId);
        if (!File.Exists(cachePath))
        {
            return false;
        }

        var cachedDocument = LoadGamelistDocument(cachePath);
        var targetPath = ResolveLiveGamelistPath(frontendSystemId);
        cancellationToken.ThrowIfCancellationRequested();

        var saved = _gamelistUpdateService.SaveExternalGamelistDocument(
            cachedDocument,
            targetPath,
            "localized-gamelist-cache-switch:" + NormalizeLanguage(language),
            cancellationToken);

        if (saved && File.Exists(targetPath))
        {
            File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(cachePath));
        }

        return saved;
    }

    private bool IsSystemCacheFresh(string language, string frontendSystemId)
    {
        var cachePath = ResolveCachedGamelistPath(language, frontendSystemId);
        var livePath = ResolveLiveGamelistPath(frontendSystemId);
        if (!File.Exists(cachePath))
        {
            return false;
        }

        if (!IsLanguageCacheSchemaCurrent(language))
        {
            return false;
        }

        if (!File.Exists(livePath))
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(livePath);
    }

    private bool IsLanguageCacheSchemaCurrent(string language)
    {
        var manifestPath = Path.Combine(ResolveLanguageCacheRoot(language), ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<LocalizedGamelistCacheManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            return manifest?.SchemaVersion >= CacheSchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task WriteManifestAsync(string language, IReadOnlyList<string> systems, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(ResolveLanguageCacheRoot(language), ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = new LocalizedGamelistCacheManifest(
            language,
            CacheSchemaVersion,
            DateTimeOffset.UtcNow,
            ResolveWindowsLanguage(),
            systems.Select(systemId =>
            {
                var cachePath = ResolveCachedGamelistPath(language, systemId);
                return new LocalizedGamelistCacheManifestSystem(
                    systemId,
                    File.Exists(cachePath) ? ComputeSha256(cachePath) : string.Empty,
                    File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0);
            }).ToList());

        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
    }

    private IEnumerable<string> EnumerateSystemsWithGamelist()
    {
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            yield break;
        }

        foreach (var systemDirectory in Directory.EnumerateDirectories(RetroBatPaths.RomsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var systemId = Path.GetFileName(systemDirectory);
            if (string.IsNullOrWhiteSpace(systemId) ||
                !File.Exists(Path.Combine(systemDirectory, "gamelist.xml")))
            {
                continue;
            }

            yield return systemId;
        }
    }

    private static List<string> ResolveSystemsToBuild(IReadOnlyCollection<string>? systemIds)
    {
        if (systemIds == null || systemIds.Count == 0)
        {
            return Directory.Exists(RetroBatPaths.RomsRoot)
                ? Directory.GetDirectories(RetroBatPaths.RomsRoot)
                    .Select(Path.GetFileName)
                    .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
                    .Select(systemId => systemId!)
                    .Where(systemId => File.Exists(ResolveLiveGamelistPath(systemId)))
                    .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
        }

        return systemIds
            .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
            .Select(systemId => systemId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(systemId => File.Exists(ResolveLiveGamelistPath(systemId)))
            .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ResolveCachedGamelistPath(string language, string frontendSystemId)
    {
        return Path.Combine(ResolveLanguageCacheRoot(language), frontendSystemId, "gamelist.xml");
    }

    private string ResolveLanguageCacheRoot(string language)
    {
        var root = _options.CurrentValue.LocalizedGamelistCache.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "resources/gamelist/localized";
        }

        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(RetroBatPaths.PluginRoot, root);
        }

        return Path.Combine(root, NormalizeLanguage(language));
    }

    private static string ResolveLiveGamelistPath(string frontendSystemId)
    {
        return Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId, "gamelist.xml");
    }

    private static XDocument LoadGamelistDocument(string gamelistPath)
    {
        using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"Invalid gamelist XML: {gamelistPath}", ex);
        }
    }

    private static void SaveGamelistDocument(XDocument document, string path)
    {
        var tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            document.Save(stream, SaveOptions.DisableFormatting);
        }

        _ = LoadGamelistDocument(tempPath);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void SetOrCreateElement(XElement parent, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var element = parent.Element(name);
        if (element == null)
        {
            parent.Add(new XElement(name, value));
            return;
        }

        element.Value = value;
    }

    private static IEnumerable<string> EnumerateSlugCandidates(string canonicalSlug, string familySlug)
    {
        yield return canonicalSlug;
        if (!string.Equals(familySlug, canonicalSlug, StringComparison.OrdinalIgnoreCase))
        {
            yield return familySlug;
        }
    }

    private XElement? FindMatchingGameNode(XElement root, LocalizedGamelistCacheRefreshEntry entry, string systemRoot)
    {
        foreach (var path in EnumeratePathCandidates(entry.GamePath, systemRoot))
        {
            var byPath = FindGameNodeByPath(root, path);
            if (byPath != null)
            {
                return byPath;
            }
        }

        var fileName = GetPortableFileName(entry.GamePath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var byFileName = root.Elements("game")
                .FirstOrDefault(node => string.Equals(
                    GetPortableFileName(node.Element("path")?.Value),
                    fileName,
                    StringComparison.OrdinalIgnoreCase));
            if (byFileName != null)
            {
                return byFileName;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.GameSlug) || !string.IsNullOrWhiteSpace(entry.GameName))
        {
            var normalizedSlug = (entry.GameSlug ?? string.Empty).Trim();
            var normalizedName = _gameNameNormalizer.NormalizeGameSlug(entry.GameName, entry.GamePath);
            return root.Elements("game")
                .FirstOrDefault(node =>
                {
                    var path = node.Element("path")?.Value;
                    var name = node.Element("name")?.Value;
                    var nodeSlug = _gameNameNormalizer.NormalizeGameSlug(name, path);
                    return
                        (!string.IsNullOrWhiteSpace(normalizedSlug) &&
                            string.Equals(nodeSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(normalizedName) &&
                            string.Equals(nodeSlug, normalizedName, StringComparison.OrdinalIgnoreCase));
                });
        }

        return null;
    }

    private static XElement? FindGameNodeByPath(XElement root, string? gamePath)
    {
        var normalizedTarget = NormalizeForCompare(gamePath);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return null;
        }

        return root.Elements("game")
            .FirstOrDefault(node => string.Equals(
                NormalizeForCompare(node.Element("path")?.Value),
                normalizedTarget,
                StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumeratePathCandidates(string? gamePath, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            yield break;
        }

        yield return gamePath.Trim();
        var relative = ToGameRelativePath(gamePath, systemRoot);
        if (!string.IsNullOrWhiteSpace(relative))
        {
            yield return relative;
        }
    }

    private static string BuildEntryRefreshKey(LocalizedGamelistCacheRefreshEntry entry)
    {
        return string.Join(
            "|",
            NormalizeForCompare(entry.FrontendSystemId),
            NormalizeForCompare(entry.GamePath),
            NormalizeSlug(entry.GameSlug),
            NormalizeSlug(entry.GameName));
    }

    private static IEnumerable<string> LocalizedTextFieldNames()
    {
        yield return "name";
        yield return "desc";
        yield return "releasedate";
        yield return "developer";
        yield return "publisher";
        yield return "players";
        yield return "lang";
        yield return "region";
        yield return "genre";
        yield return "family";
        yield return "genres";
        yield return "rating";
    }

    private static void AddLanguage(ICollection<string> result, string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (string.IsNullOrWhiteSpace(normalized) || !SupportedLanguages.Contains(normalized))
        {
            return;
        }

        if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(normalized);
        }
    }

    private static string ResolveWindowsLanguage()
    {
        return NormalizeLanguage(CultureInfo.CurrentUICulture.Name) ??
            NormalizeLanguage(CultureInfo.CurrentCulture.Name) ??
            NormalizeLanguage(CultureInfo.InstalledUICulture.Name) ??
            "en";
    }

    private static string NormalizeLanguage(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        return normalized.Length >= 2 ? normalized[..2] : string.Empty;
    }

    private static string NormalizePathKey(string? path)
    {
        return (path ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
    }

    private static string ToGameRelativePath(string? gamePath, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        if (!Path.IsPathRooted(gamePath))
        {
            return EnsureEsRelativePrefix(gamePath);
        }

        var relative = Path.GetRelativePath(systemRoot, gamePath).Replace('\\', '/');
        return EnsureEsRelativePrefix(relative);
    }

    private static string EnsureEsRelativePrefix(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            return normalized;
        }

        return "./" + normalized.TrimStart('/');
    }

    private static string NormalizeForCompare(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();
    }

    private static string NormalizeSlug(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var chars = normalized
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars)
            .Trim('_')
            .Replace("__", "_", StringComparison.Ordinal);
    }

    private static string GetPortableFileName(string? path)
    {
        return Path.GetFileName((path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task TryAppendCacheLogAsync(
        string phase,
        string status,
        object details,
        CancellationToken cancellationToken)
    {
        try
        {
            var logDirectory = Path.Combine(RetroBatPaths.PluginRoot, "logs");
            Directory.CreateDirectory(logDirectory);
            var payload = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                phase,
                status,
                details
            };
            var line = JsonSerializer.Serialize(payload, JsonLineOptions) + Environment.NewLine;
            await LogGate.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(Path.Combine(logDirectory, CacheLogFileName), line, cancellationToken);
            }
            finally
            {
                LogGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private sealed record LocalizedGamelistEntryPatchSystemResult(
        int EntriesPatched,
        int EntriesSkipped,
        bool FileSaved);
}

public sealed record LocalizedGamelistSwitchResult(
    string Language,
    int SystemsSwitched,
    int SystemsGenerated,
    int SystemsFailed,
    bool Success,
    string Reason);

public sealed record LocalizedGamelistPrebuildResult(
    IReadOnlyList<string> Languages,
    int SystemsGenerated,
    int SystemsFailed,
    string Reason);

public sealed record LocalizedGamelistEntryPatchResult(
    IReadOnlyList<string> Languages,
    int EntriesRequested,
    int EntriesPatched,
    int EntriesSkipped,
    int FilesSaved,
    int FilesFailed,
    string Reason);

public sealed record LocalizedGamelistCacheManifest(
    string Language,
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string WindowsLanguage,
    IReadOnlyList<LocalizedGamelistCacheManifestSystem> Systems);

public sealed record LocalizedGamelistCacheManifestSystem(
    string SystemId,
    string Sha256,
    long Bytes);
