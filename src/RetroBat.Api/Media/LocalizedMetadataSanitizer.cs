using System.Net;
using System.Text;

namespace RetroBat.Api.Media;

public static class LocalizedMetadataSanitizer
{
    private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);

    private static readonly HashSet<string> LanguageCodes =
    [
        "ar", "bg", "cs", "da", "de", "el", "en", "es", "fi", "fr", "it", "ja",
        "ko", "nl", "no", "pl", "pt", "ru", "sv", "tr", "uk", "us", "zh"
    ];

    private static readonly Dictionary<string, string> FrenchGenreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["action"] = "Action",
        ["adventure"] = "Aventure",
        ["accion"] = "Action",
        ["acao"] = "Action",
        ["accao"] = "Action",
        ["azione"] = "Action",
        ["avventura"] = "Aventure",
        ["beat'em all"] = "Beat'em All",
        ["board game"] = "Jeu de societe",
        ["board games"] = "Jeu de societe",
        ["combattimento"] = "Combat",
        ["combattimento a scorrimento"] = "Fighter Scrolling",
        ["compilation"] = "Compilation",
        ["corri e salta a scorrimento"] = "Plateforme",
        ["course"] = "Course",
        ["carreras"] = "Course",
        ["corsa"] = "Course",
        ["corrida"] = "Course",
        ["driving"] = "Conduite",
        ["fahren"] = "Conduite",
        ["fighting"] = "Combat",
        ["fighting scrolling"] = "Fighter Scrolling",
        ["fighter scrolling"] = "Fighter Scrolling",
        ["gioco da tavolo"] = "Jeu de societe",
        ["gioco di ruolo"] = "Jeu de roles",
        ["kampfe"] = "Combat",
        ["kampf"] = "Combat",
        ["labyrinth"] = "Labyrinthe",
        ["lucha"] = "Combat",
        ["plane"] = "Avion",
        ["piattaforma"] = "Plateforme",
        ["plataforma"] = "Plateforme",
        ["plataformas"] = "Plateforme",
        ["platform"] = "Plateforme",
        ["plattform"] = "Plateforme",
        ["puzzle"] = "Puzzle",
        ["racing"] = "Course",
        ["role playing game"] = "Jeu de roles",
        ["role playing games"] = "Jeu de roles",
        ["rollenspiele"] = "Jeu de roles",
        ["sammlung"] = "Compilation",
        ["shooter"] = "Tir",
        ["shooting"] = "Tir",
        ["sparo"] = "Tir",
        ["sparatutto a scorrimento"] = "Shooter Scrolling",
        ["sport"] = "Sport",
        ["sports"] = "Sport",
        ["tiro"] = "Tir"
    };

    private static readonly Dictionary<string, string> EnglishGenreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["action"] = "Action",
        ["aventure"] = "Adventure",
        ["adventure"] = "Adventure",
        ["avion"] = "Plane",
        ["beat'em all"] = "Beat'em Up",
        ["beat'em up"] = "Beat'em Up",
        ["boxe"] = "Boxing",
        ["boxing"] = "Boxing",
        ["combat"] = "Fighting",
        ["compilation"] = "Compilation",
        ["conduite"] = "Driving",
        ["course"] = "Racing",
        ["divers"] = "Miscellaneous",
        ["driving"] = "Driving",
        ["fight"] = "Fighting",
        ["fighting"] = "Fighting",
        ["flipper"] = "Pinball",
        ["gestion"] = "Management",
        ["jeu de role"] = "Role Playing Game",
        ["jeu de roles"] = "Role Playing Game",
        ["jeu de r\u00f4le"] = "Role Playing Game",
        ["jeu de r\u00f4les"] = "Role Playing Game",
        ["jeu de societe"] = "Board Game",
        ["jeu de soci\u00e9t\u00e9"] = "Board Game",
        ["labyrinthe"] = "Maze",
        ["lightgun shooter"] = "Lightgun Shooter",
        ["multisport"] = "Multisport",
        ["multisports"] = "Multisport",
        ["pinball"] = "Pinball",
        ["plateforme"] = "Platform",
        ["platform"] = "Platform",
        ["platformer"] = "Platform",
        ["puzzle"] = "Puzzle",
        ["puzzle-game"] = "Puzzle",
        ["racing"] = "Racing",
        ["reflexion"] = "Puzzle",
        ["r\u00e9flexion"] = "Puzzle",
        ["role playing game"] = "Role Playing Game",
        ["shooter"] = "Shooter",
        ["shoot'em up"] = "Shoot'em Up",
        ["shooting"] = "Shooter",
        ["sport"] = "Sports",
        ["sports"] = "Sports",
        ["tir"] = "Shooter",
        ["tir avec accessoire"] = "Lightgun Shooter",
        ["vertical"] = "Vertical"
    };

    public static string SanitizeField(string fieldName, string? value, string? preferredLanguage = null)
    {
        var normalized = SanitizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (string.Equals(fieldName, "family", StringComparison.OrdinalIgnoreCase))
        {
            return SanitizeLocalizedCommaValue(normalized, preferredLanguage);
        }

        if (string.Equals(fieldName, "genre", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "genres", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeGenreAliases(SanitizeLocalizedCommaValue(normalized, preferredLanguage, keepAllUsefulTokens: true), preferredLanguage);
        }

        return normalized;
    }

    public static string SanitizeText(string? value)
    {
        return Decode(value);
    }

    private static string SanitizeLocalizedCommaValue(string value, string? preferredLanguage, bool keepAllUsefulTokens = false)
    {
        var tokens = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeText)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();

        if (tokens.Count < 2)
        {
            return value;
        }

        var language = NormalizeLanguage(preferredLanguage);
        var languageIndex = string.IsNullOrWhiteSpace(language)
            ? -1
            : tokens.FindIndex(token => string.Equals(token, language, StringComparison.OrdinalIgnoreCase));

        if (languageIndex >= 0 && languageIndex + 1 < tokens.Count)
        {
            var localized = tokens
                .Skip(languageIndex + 1)
                .TakeWhile(token => !IsLanguageCode(token))
                .Where(IsUsefulToken)
                .ToList();

            if (localized.Count > 0)
            {
                return string.Join(", ", localized);
            }
        }

        var useful = tokens
            .SkipWhile(token => IsNoiseToken(token))
            .Where(token => keepAllUsefulTokens || !IsNoiseToken(token))
            .Where(IsUsefulToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keepAllUsefulTokens && ShouldCollapseLocalizedGenreCollection(useful))
        {
            return NormalizeGenreAliasValue(
                useful[0],
                string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                    ? EnglishGenreAliases
                    : FrenchGenreAliases);
        }

        return useful.Count > 0 ? string.Join(", ", useful) : value;
    }

    private static bool ShouldCollapseLocalizedGenreCollection(IReadOnlyList<string> tokens)
    {
        return tokens.Count >= 4;
    }

    private static string NormalizeGenreAliases(string value, string? preferredLanguage)
    {
        var language = NormalizeLanguage(preferredLanguage);
        var aliases = string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase)
            ? FrenchGenreAliases
            : string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                ? EnglishGenreAliases
                : null;
        if (aliases == null)
        {
            return value;
        }

        var normalizedValues = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => NormalizeGenreAliasValue(value, aliases))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalizedValues.Count > 0 ? string.Join(", ", normalizedValues) : value;
    }

    private static string NormalizeGenreAliasValue(string value, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var parts = value
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => NormalizeGenreAliasToken(token, aliases))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count > 0 ? string.Join(" / ", parts) : value;
    }

    private static string NormalizeGenreAliasToken(string value, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var normalized = SanitizeText(value);
        var map = aliases ?? FrenchGenreAliases;
        return map.TryGetValue(normalized, out var mapped)
            ? mapped
            : normalized;
    }

    private static bool IsUsefulToken(string token)
    {
        return !string.IsNullOrWhiteSpace(token) && !IsNoiseToken(token);
    }

    private static bool IsNoiseToken(string token)
    {
        var normalized = token.Trim();
        if (normalized.All(char.IsDigit))
        {
            return true;
        }

        return IsLanguageCode(normalized);
    }

    private static bool IsLanguageCode(string token)
    {
        return LanguageCodes.Contains(NormalizeLanguage(token));
    }

    private static string NormalizeLanguage(string? rawLanguage)
    {
        var normalized = (rawLanguage ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized.Length >= 2 ? normalized[..2] : normalized;
    }

    private static string Decode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        for (var i = 0; i < 2 && normalized.Contains('&', StringComparison.Ordinal); i++)
        {
            var decoded = WebUtility.HtmlDecode(normalized);
            if (string.Equals(decoded, normalized, StringComparison.Ordinal))
            {
                break;
            }

            normalized = decoded;
        }

        normalized = RepairLikelyMojibake(normalized);
        return normalized.Trim();
    }

    private static string RepairLikelyMojibake(string value)
    {
        var best = value;
        var bestScore = MojibakeScore(best);
        if (bestScore == 0)
        {
            return best;
        }

        for (var i = 0; i < 2; i++)
        {
            var repaired = TryDecodeWindows1252AsUtf8(best);
            if (string.Equals(repaired, best, StringComparison.Ordinal))
            {
                break;
            }

            var repairedScore = MojibakeScore(repaired);
            if (repairedScore >= bestScore)
            {
                break;
            }

            best = repaired;
            bestScore = repairedScore;
            if (bestScore == 0)
            {
                break;
            }
        }

        return best;
    }

    private static string TryDecodeWindows1252AsUtf8(string value)
    {
        try
        {
            var bytes = new byte[value.Length];
            for (var i = 0; i < value.Length; i++)
            {
                if (!TryGetWindows1252Byte(value[i], out bytes[i]))
                {
                    return value;
                }
            }

            var repaired = StrictUtf8Encoding.GetString(bytes);
            if (!value.Contains('?', StringComparison.Ordinal) && repaired.Contains('?', StringComparison.Ordinal))
            {
                return value;
            }

            return repaired;
        }
        catch (DecoderFallbackException)
        {
            return value;
        }
        catch (EncoderFallbackException)
        {
            return value;
        }
    }

    private static bool TryGetWindows1252Byte(char character, out byte value)
    {
        switch (character)
        {
            case '\u20ac': value = 0x80; return true;
            case '\u201a': value = 0x82; return true;
            case '\u0192': value = 0x83; return true;
            case '\u201e': value = 0x84; return true;
            case '\u2026': value = 0x85; return true;
            case '\u2020': value = 0x86; return true;
            case '\u2021': value = 0x87; return true;
            case '\u02c6': value = 0x88; return true;
            case '\u2030': value = 0x89; return true;
            case '\u0160': value = 0x8A; return true;
            case '\u2039': value = 0x8B; return true;
            case '\u0152': value = 0x8C; return true;
            case '\u017d': value = 0x8E; return true;
            case '\u2018': value = 0x91; return true;
            case '\u2019': value = 0x92; return true;
            case '\u201c': value = 0x93; return true;
            case '\u201d': value = 0x94; return true;
            case '\u2022': value = 0x95; return true;
            case '\u2013': value = 0x96; return true;
            case '\u2014': value = 0x97; return true;
            case '\u02dc': value = 0x98; return true;
            case '\u2122': value = 0x99; return true;
            case '\u0161': value = 0x9A; return true;
            case '\u203a': value = 0x9B; return true;
            case '\u0153': value = 0x9C; return true;
            case '\u017e': value = 0x9E; return true;
            case '\u0178': value = 0x9F; return true;
        }

        if (character <= byte.MaxValue)
        {
            value = (byte)character;
            return true;
        }

        value = 0;
        return false;
    }

    private static int MojibakeScore(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var score = 0;
        score += Count(value, "\u00c3") * 3;
        score += Count(value, "\u00c2") * 3;
        score += Count(value, "\u00e2\u20ac") * 4;
        score += Count(value, "\ufffd") * 8;
        score += Count(value, "\u0080") * 4;
        score += Count(value, "\u009d") * 4;
        return score;
    }

    private static int Count(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}
