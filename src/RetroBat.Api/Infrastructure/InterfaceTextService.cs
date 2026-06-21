using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class InterfaceTextService
{
    private const string DefaultLanguage = "en";
    private readonly object _lock = new();
    private readonly string _dictionaryPath = Path.Combine(RetroBatPaths.PluginRoot, "resources", "locales", "interface-texts.json");
    private Dictionary<string, Dictionary<string, string>> _texts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public string Text(string key, string? language)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var texts = LoadTexts();
        foreach (var candidate in EnumerateLanguageCandidates(language))
        {
            if (texts.TryGetValue(candidate, out var languageTexts) &&
                languageTexts.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return key;
    }

    public string Format(string key, string? language, params (string Name, object? Value)[] args)
    {
        var value = Text(key, language);
        foreach (var (name, rawValue) in args)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            value = value.Replace("{" + name + "}", Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return value;
    }

    private IReadOnlyDictionary<string, Dictionary<string, string>> LoadTexts()
    {
        lock (_lock)
        {
            if (!File.Exists(_dictionaryPath))
            {
                _texts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                _lastWriteUtc = DateTime.MinValue;
                return _texts;
            }

            var lastWriteUtc = File.GetLastWriteTimeUtc(_dictionaryPath);
            if (_texts.Count > 0 && lastWriteUtc == _lastWriteUtc)
            {
                return _texts;
            }

            try
            {
                var json = File.ReadAllText(_dictionaryPath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? new();
                _texts = parsed.ToDictionary(
                    pair => NormalizeLanguageKey(pair.Key),
                    pair => new Dictionary<string, string>(pair.Value ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                _lastWriteUtc = lastWriteUtc;
            }
            catch (JsonException)
            {
                _texts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                _lastWriteUtc = lastWriteUtc;
            }

            return _texts;
        }
    }

    private static IEnumerable<string> EnumerateLanguageCandidates(string? language)
    {
        var normalized = NormalizeLanguageKey(language);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
            var separatorIndex = normalized.IndexOf('_');
            if (separatorIndex > 0)
            {
                yield return normalized[..separatorIndex];
            }
        }

        yield return DefaultLanguage;
    }

    private static string NormalizeLanguageKey(string? language)
    {
        return (language ?? string.Empty)
            .Trim()
            .Replace('-', '_')
            .ToLowerInvariant();
    }
}
