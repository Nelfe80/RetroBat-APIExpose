using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// The downloaded translation engine is an executable: extracted only with a configured, matching SHA-256.
/// </summary>
public class TranslationEngineHashTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Matching_hash_is_accepted_whatever_the_case()
    {
        Assert.True(DescriptionTranslationService.EngineArchiveAccepted(Hash, Hash.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Missing_or_wrong_hash_is_refused(string? expected)
    {
        Assert.False(DescriptionTranslationService.EngineArchiveAccepted(expected, Hash));
    }
}
