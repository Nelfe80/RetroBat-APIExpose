using RetroBat.Domain.Models;

namespace RetroBat.Domain.Interfaces;

public interface ILocalizedTextStore
{
    Task<IReadOnlyList<LocalizedTextRecord>> PersistAsync(string systemId, string gameSlug, GameDetails? details, CancellationToken cancellationToken = default);
    Task<bool> PersistFieldsAsync(string systemId, string gameSlug, string language, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);
    Task<LocalizedTextBundle?> LoadPreferredBundleAsync(
        string systemId,
        string gameSlug,
        string requestedLanguage,
        CancellationToken cancellationToken = default,
        bool allowAnyLanguageFallback = true,
        bool allowEnglishFallback = true);
}
