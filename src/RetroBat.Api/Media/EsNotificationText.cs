using System.Globalization;
using System.Text;

namespace RetroBat.Api.Media;

internal static class EsNotificationText
{
    private const int MaxGameNameLength = 20;
    private const int DefaultMaxPopupLength = 160;

    public static string ShortGameName(string? value, string fallback = "jeu")
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return fallback;
        }

        normalized = StripTrailingParentheticalMetadata(normalized);
        if (normalized.Length <= MaxGameNameLength)
        {
            return SanitizeForEs(normalized, MaxGameNameLength, fallback);
        }

        var shortened = normalized[..Math.Max(8, MaxGameNameLength - 3)].TrimEnd() + "...";
        return SanitizeForEs(shortened, MaxGameNameLength, fallback);
    }

    public static string SanitizeForEsPopup(string? value)
    {
        return SanitizeForEs(value, DefaultMaxPopupLength);
    }

    public static string SanitizeForEs(string? value, int? maxLength = null, string fallback = "")
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return fallback;
        }

        normalized = TransliterateLatin(ReplaceTypography(normalized)).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
                continue;
            }

            if (character >= 32 && character <= 126)
            {
                builder.Append(character);
            }
        }

        var sanitized = builder.ToString().Normalize(NormalizationForm.FormC).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback;
        }

        if (maxLength is > 3 && sanitized.Length > maxLength.Value)
        {
            sanitized = sanitized[..(maxLength.Value - 3)].TrimEnd() + "...";
        }

        return sanitized;
    }

    private static string ReplaceTypography(string value)
    {
        return value
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('´', '\'')
            .Replace('`', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('«', '"')
            .Replace('»', '"')
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('…', '.');
    }

    private static string TransliterateLatin(string value)
    {
        return value
            .Replace("Æ", "AE", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("Œ", "OE", StringComparison.Ordinal)
            .Replace("œ", "oe", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("Ø", "O", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.Ordinal)
            .Replace("Ð", "D", StringComparison.Ordinal)
            .Replace("ð", "d", StringComparison.Ordinal)
            .Replace("Þ", "Th", StringComparison.Ordinal)
            .Replace("þ", "th", StringComparison.Ordinal)
            .Replace("Ł", "L", StringComparison.Ordinal)
            .Replace("ł", "l", StringComparison.Ordinal)
            .Replace("Đ", "D", StringComparison.Ordinal)
            .Replace("đ", "d", StringComparison.Ordinal);
    }

    private static string StripTrailingParentheticalMetadata(string value)
    {
        var current = value.Trim();
        while (current.EndsWith(")", StringComparison.Ordinal))
        {
            var start = current.LastIndexOf('(');
            if (start <= 0)
            {
                break;
            }

            var suffix = current[start..];
            if (!LooksLikeGameMetadataSuffix(suffix))
            {
                break;
            }

            current = current[..start].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(current) ? value.Trim() : current;
    }

    private static bool LooksLikeGameMetadataSuffix(string suffix)
    {
        var text = suffix.Trim('(', ')', ' ');
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains(','))
        {
            return true;
        }

        return text.Contains("Europe", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("USA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("World", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("France", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Japan", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SGB", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GB Compatible", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Rev ", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("En", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Fr", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("De", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Es", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("It", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Nl", StringComparison.OrdinalIgnoreCase);
    }
}
