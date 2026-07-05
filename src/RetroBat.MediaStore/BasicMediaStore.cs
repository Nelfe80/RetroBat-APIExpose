using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.MediaStore;

public class BasicMediaStore : IMediaStore
{
    public Task<string?> ResolveAsync(string gameRef, string assetKind)
    {
        if (string.IsNullOrWhiteSpace(gameRef))
        {
            return Task.FromResult<string?>(null);
        }

        var normalizedPath = gameRef.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(RetroBatPaths.MediaRoot, normalizedPath));
        var mediaRoot = Path.GetFullPath(RetroBatPaths.MediaRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(mediaRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(candidate);
    }
}
