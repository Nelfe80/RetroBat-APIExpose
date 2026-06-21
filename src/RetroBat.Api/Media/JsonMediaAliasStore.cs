using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public class JsonMediaAliasStore : IMediaAliasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int FileIoRetryCount = 5;
    private static readonly TimeSpan FileIoRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> ResolveGameSlugAsync(string systemId, IEnumerable<string> aliasKeys, string fallbackSlug, CancellationToken cancellationToken = default)
    {
        var normalizedKeys = NormalizeKeys(aliasKeys);
        if (normalizedKeys.Count == 0)
        {
            return fallbackSlug;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = await LoadIndexAsync(GetGameAliasFilePath(systemId), cancellationToken);
            foreach (var key in normalizedKeys)
            {
                if (index.Entries.TryGetValue(key, out var canonical) && !string.IsNullOrWhiteSpace(canonical))
                {
                    return canonical;
                }
            }

            return fallbackSlug;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordGameAliasesAsync(string systemId, string canonicalSlug, IEnumerable<string> aliasKeys, CancellationToken cancellationToken = default)
    {
        var normalizedKeys = NormalizeKeys(aliasKeys);
        if (string.IsNullOrWhiteSpace(canonicalSlug) || normalizedKeys.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var filePath = GetGameAliasFilePath(systemId);
            var index = await LoadIndexAsync(filePath, cancellationToken);
            var updated = false;
            foreach (var key in normalizedKeys)
            {
                if (index.Entries.TryGetValue(key, out var existingCanonical) &&
                    !string.IsNullOrWhiteSpace(existingCanonical))
                {
                    continue;
                }

                index.Entries[key] = canonicalSlug;
                updated = true;
            }

            if (updated)
            {
                await SaveIndexAsync(filePath, index, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ResolveMediaByHashAsync(string systemId, string kind, string contentHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(contentHash))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = await LoadIndexAsync(GetMediaHashFilePath(), cancellationToken);
            var key = BuildMediaHashKey(systemId, kind, contentHash);
            if (!index.Entries.TryGetValue(key, out var relativePath))
            {
                var legacyKey = BuildLegacyCompatibleMediaHashKey(systemId, kind, contentHash);
                if (string.IsNullOrWhiteSpace(legacyKey) || !index.Entries.TryGetValue(legacyKey, out relativePath))
                {
                    return null;
                }
            }

            return Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(RetroBatPaths.MediaRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordMediaHashAsync(string systemId, string kind, string contentHash, string canonicalPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(contentHash) || string.IsNullOrWhiteSpace(canonicalPath))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var filePath = GetMediaHashFilePath();
            var index = await LoadIndexAsync(filePath, cancellationToken);
            index.Entries[BuildMediaHashKey(systemId, kind, contentHash)] = ToMediaRelativePath(canonicalPath);
            await SaveIndexAsync(filePath, index, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string BuildMediaHashKey(string systemId, string kind, string contentHash)
    {
        return $"{systemId.Trim().ToLowerInvariant()}|{MediaKinds.Normalize(kind)}|{contentHash.Trim().ToUpperInvariant()}";
    }

    private static string? BuildLegacyCompatibleMediaHashKey(string systemId, string kind, string contentHash)
    {
        var normalizedKind = MediaKinds.Normalize(kind);
        var legacyKind = normalizedKind switch
        {
            MediaKinds.ScreenMarquee => MediaKinds.LegacyScreenMarquee,
            MediaKinds.BoxBack => MediaKinds.LegacyBoxBack,
            _ => null
        };

        return legacyKind == null
            ? null
            : $"{systemId.Trim().ToLowerInvariant()}|{legacyKind}|{contentHash.Trim().ToUpperInvariant()}";
    }

    private static string GetGameAliasFilePath(string systemId)
    {
        Directory.CreateDirectory(RetroBatPaths.MediaAliasesGamesRoot);
        return Path.Combine(RetroBatPaths.MediaAliasesGamesRoot, $"{systemId}.json");
    }

    private static string GetMediaHashFilePath()
    {
        Directory.CreateDirectory(RetroBatPaths.MediaAliasesSharedRoot);
        return Path.Combine(RetroBatPaths.MediaAliasesSharedRoot, "media-hashes.json");
    }

    private static List<string> NormalizeKeys(IEnumerable<string> aliasKeys)
    {
        return aliasKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ToMediaRelativePath(string canonicalPath)
    {
        var fullCanonical = Path.GetFullPath(canonicalPath);
        var fullMediaRoot = Path.GetFullPath(RetroBatPaths.MediaRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (fullCanonical.StartsWith(fullMediaRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(RetroBatPaths.MediaRoot, canonicalPath).Replace('\\', '/');
        }

        return canonicalPath;
    }

    private static async Task<MediaAliasIndex> LoadIndexAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new MediaAliasIndex();
        }

        return await ExecuteWithFileRetryAsync(async () =>
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var index = await JsonSerializer.DeserializeAsync<MediaAliasIndex>(stream, JsonOptions, cancellationToken);
            return index ?? new MediaAliasIndex();
        }, cancellationToken);
    }

    private static async Task SaveIndexAsync(string filePath, MediaAliasIndex index, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await ExecuteWithFileRetryAsync(async () =>
        {
            var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
                }

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }, cancellationToken);
    }

    private static async Task ExecuteWithFileRetryAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteWithFileRetryAsync(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);
    }

    private static async Task<T> ExecuteWithFileRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (IOException) when (attempt < FileIoRetryCount)
            {
                await Task.Delay(FileIoRetryDelay, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < FileIoRetryCount)
            {
                await Task.Delay(FileIoRetryDelay, cancellationToken);
            }
        }
    }
}
