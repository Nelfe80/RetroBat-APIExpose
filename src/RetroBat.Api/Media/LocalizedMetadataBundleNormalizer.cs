using RetroBat.Domain.Models;

namespace RetroBat.Api.Media;

public static class LocalizedMetadataBundleNormalizer
{
    public static bool NormalizeBundleFields(LocalizedTextBundle bundle, string fallbackLanguage)
    {
        var changed = false;
        var normalizedLanguage = NormalizeLanguage(string.IsNullOrWhiteSpace(bundle.Language) ? fallbackLanguage : bundle.Language);
        if (!string.Equals(bundle.Language, normalizedLanguage, StringComparison.Ordinal))
        {
            bundle.Language = normalizedLanguage;
            changed = true;
        }

        if (bundle.Fields.Count == 0)
        {
            return changed;
        }

        foreach (var field in bundle.Fields.ToList())
        {
            var normalizedField = NormalizeFieldName(field.Key);
            var normalizedValue = LocalizedMetadataSanitizer.SanitizeField(normalizedField, field.Value, bundle.Language);

            if (!string.Equals(normalizedField, field.Key, StringComparison.OrdinalIgnoreCase))
            {
                bundle.Fields.Remove(field.Key);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                continue;
            }

            if (!bundle.Fields.TryGetValue(normalizedField, out var existing) ||
                !string.Equals(existing, normalizedValue, StringComparison.Ordinal))
            {
                bundle.Fields[normalizedField] = normalizedValue;
                changed = true;
            }
        }

        return changed;
    }

    public static string NormalizeFieldName(string rawField)
    {
        var normalized = (rawField ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "description" => "desc",
            "instructions_text" => "instructions",
            _ => normalized
        };
    }

    public static string NormalizeLanguage(string? rawLanguage)
    {
        var normalized = (rawLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length >= 2)
        {
            return normalized[..2];
        }

        return "en";
    }
}
