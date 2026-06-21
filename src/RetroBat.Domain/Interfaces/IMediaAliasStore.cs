namespace RetroBat.Domain.Interfaces;

public interface IMediaAliasStore
{
    Task<string> ResolveGameSlugAsync(string systemId, IEnumerable<string> aliasKeys, string fallbackSlug, CancellationToken cancellationToken = default);
    Task RecordGameAliasesAsync(string systemId, string canonicalSlug, IEnumerable<string> aliasKeys, CancellationToken cancellationToken = default);
    Task<string?> ResolveMediaByHashAsync(string systemId, string kind, string contentHash, CancellationToken cancellationToken = default);
    Task RecordMediaHashAsync(string systemId, string kind, string contentHash, string canonicalPath, CancellationToken cancellationToken = default);
}
