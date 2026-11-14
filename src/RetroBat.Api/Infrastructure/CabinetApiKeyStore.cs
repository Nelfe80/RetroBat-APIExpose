using System.Security.Cryptography;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Per-cabinet API key. The shipped appsettings ships NO key (never commit a secret);
/// when <c>Security:ApiKey</c> is empty this generates a unique key on first run and
/// persists it under <c>state/</c>, so non-loopback callers (the Fleet Hub over the LAN)
/// stay gated with a key that differs on every machine. Loopback callers are never gated,
/// and a key that cannot be written is kept in memory for this run: network callers then
/// stay refused instead of finding the API open.
/// </summary>
public static class CabinetApiKeyStore
{
    private static string KeyPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "cabinet-api-key.txt");

    private static string? _inMemoryKey;

    /// <summary>Returns the persisted key, creating one the first time. When the file cannot be
    /// read nor written, a random key kept in memory for this run (never empty: an empty key
    /// would remove the network guard).</summary>
    public static string GetOrCreate() => GetOrCreate(KeyPath);

    internal static string GetOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16)
                {
                    return existing;
                }
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)); // 40 hex chars
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, key);
            return key;
        }
        catch
        {
            return _inMemoryKey ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        }
    }
}
