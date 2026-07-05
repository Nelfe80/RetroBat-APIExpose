using System.Text.RegularExpressions;

namespace RetroBat.Api.Media;

public partial class GameNameNormalizer
{
    public string NormalizeDisplayName(string? gameName, string? gamePath = null)
    {
        return NormalizeDisplayNameValue(gameName, gamePath);
    }

    public static string NormalizeDisplayNameValue(string? gameName, string? gamePath = null)
    {
        var seed = !string.IsNullOrWhiteSpace(gameName)
            ? gameName.Trim()
            : Path.GetFileNameWithoutExtension(gamePath ?? string.Empty);

        if (string.IsNullOrWhiteSpace(seed))
        {
            return string.Empty;
        }

        var normalized = LocalizedMetadataSanitizer.SanitizeText(seed);
        normalized = StripTrailingRomMetadata(normalized);
        normalized = MultiSpaceRegex().Replace(normalized, " ").Trim();

        return string.IsNullOrWhiteSpace(normalized) ? seed.Trim() : normalized;
    }

    public string NormalizeGameSlug(string? gameName, string? gamePath)
    {
        var pathSeed = Path.GetFileNameWithoutExtension(gamePath ?? string.Empty);
        var seed = !string.IsNullOrWhiteSpace(pathSeed)
            ? pathSeed
            : gameName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(seed))
        {
            return string.Empty;
        }

        var cleaned = BracketedContentRegex().Replace(seed, " ");
        cleaned = cleaned.Replace('&', ' ');
        cleaned = NonAlphaNumericRegex().Replace(cleaned, " ");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim().ToLowerInvariant();

        return cleaned.Replace(' ', '_');
    }

    private static string StripTrailingRomMetadata(string value)
    {
        var current = value.Trim();
        while (current.EndsWith(")", StringComparison.Ordinal))
        {
            var start = current.LastIndexOf('(');
            if (start <= 0)
            {
                break;
            }

            var suffix = current[(start + 1)..^1].Trim();
            if (!LooksLikeRomMetadataSuffix(suffix))
            {
                break;
            }

            current = current[..start].TrimEnd();
        }

        return current;
    }

    private static bool LooksLikeRomMetadataSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (UpperCodeRegex().IsMatch(normalized))
        {
            return true;
        }

        if (KnownRomMetadataPhraseRegex().IsMatch(normalized))
        {
            return true;
        }

        var tokens = normalized
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens.All(IsRomMetadataToken);
    }

    private static bool IsRomMetadataToken(string token)
    {
        var normalized = token.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return KnownRomMetadataTokenRegex().IsMatch(normalized) ||
            LanguageCodeRegex().IsMatch(normalized) ||
            UpperCodeRegex().IsMatch(normalized);
    }

    [GeneratedRegex(@"\(.*?\)|\[.*?\]", RegexOptions.Compiled)]
    private static partial Regex BracketedContentRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^[A-Z0-9]{2,8}$", RegexOptions.Compiled)]
    private static partial Regex UpperCodeRegex();

    [GeneratedRegex(@"^(En|Fr|De|Es|It|Nl|Sv|Pt|Br|No|Da|Fi|Ru|Ja|Jp|Ko|Zh|Cn|Pl|Cs|Uk)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LanguageCodeRegex();

    [GeneratedRegex(@"^(USA|US|Europe|World|Japan|France|Germany|Spain|Italy|Netherlands|Sweden|Australia|Brazil|Korea|China|Asia|Canada)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex KnownRomMetadataTokenRegex();

    [GeneratedRegex(@"^(GB Compatible|SGB Enhanced|Rev(?:ision)?\s*[A-Z0-9.]*|Beta|Proto(?:type)?|Demo|Sample|Unl|Unlicensed|Pirate|Hack|Alt|Virtual Console|Aftermarket)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex KnownRomMetadataPhraseRegex();
}
