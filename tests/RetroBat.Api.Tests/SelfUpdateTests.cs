using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La lecture d'une version. C'est elle qui decide s'il faut se mettre a jour : une version mal
/// lue, et une borne se reinstallerait en boucle ou ne se mettrait jamais a jour.
/// </summary>
public class SelfUpdateTests
{
    [Theory]
    [InlineData("v1.8.3", "1.8.3")]
    [InlineData("1.8.3", "1.8.3")]
    [InlineData("1.8.3.0", "1.8.3")]
    [InlineData("1.8.3+20260909.123216.01aa91a7", "1.8.3")]   // ce que porte l'exe publie
    [InlineData("APIExpose 1.9", "1.9.0")]
    [InlineData("1.10.0", "1.10.0")]
    public void Une_version_se_lit_sous_toutes_ses_formes(string texte, string attendu)
        => Assert.Equal(attendu, SelfUpdateService.LireVersion(texte)?.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void Ce_qui_n_est_pas_une_version_ne_se_lit_pas(string? texte)
        => Assert.Null(SelfUpdateService.LireVersion(texte!));

    [Fact]
    public void Les_versions_se_comparent_par_nombre_pas_par_texte()
    {
        // « 1.10.0 » vient APRES « 1.9.0 », ce qu'une comparaison de chaines dirait a l'envers.
        var neuf = SelfUpdateService.LireVersion("v1.9.0")!;
        var dix = SelfUpdateService.LireVersion("v1.10.0")!;
        Assert.True(dix > neuf);
        Assert.True(SelfUpdateService.LireVersion("1.8.4")! > SelfUpdateService.LireVersion("1.8.3+build")!);
        Assert.False(SelfUpdateService.LireVersion("1.8.3")! > SelfUpdateService.LireVersion("1.8.3.0")!);
    }
}
