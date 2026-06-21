namespace RetroBat.Domain.Interfaces;

public interface IMediaStore
{
    Task<string?> ResolveAsync(string gameRef, string assetKind);
}
