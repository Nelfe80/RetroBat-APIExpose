using System.IO;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// The cabinet key is never empty: an empty key removes the network guard.
/// </summary>
public class CabinetApiKeyStoreTests
{
    [Fact]
    public void A_key_is_created_then_read_back()
    {
        var folder = Directory.CreateTempSubdirectory("cabinet-key-");
        try
        {
            var path = Path.Combine(folder.FullName, "state", "cabinet-api-key.txt");
            var first = CabinetApiKeyStore.GetOrCreate(path);
            Assert.Equal(40, first.Length);
            Assert.Equal(first, CabinetApiKeyStore.GetOrCreate(path));
            Assert.Equal(first, File.ReadAllText(path).Trim());
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void A_key_that_cannot_be_written_stays_in_memory_and_is_never_empty()
    {
        var folder = Directory.CreateTempSubdirectory("cabinet-key-");
        try
        {
            // A FILE where the folder should be: the key cannot be written.
            var blocker = Path.Combine(folder.FullName, "state");
            File.WriteAllText(blocker, "x");
            var path = Path.Combine(blocker, "cabinet-api-key.txt");
            var key = CabinetApiKeyStore.GetOrCreate(path);
            Assert.False(string.IsNullOrEmpty(key));
            Assert.Equal(key, CabinetApiKeyStore.GetOrCreate(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }
}
