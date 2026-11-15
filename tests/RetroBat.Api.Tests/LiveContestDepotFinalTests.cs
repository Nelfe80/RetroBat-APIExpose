using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

public sealed class LiveContestDepotFinalTests
{
    [Theory]
    [InlineData(200, "Recu")]
    [InlineData(204, "Recu")]
    // LiveContest 2e2cfd6 : 400 « contest inactif » = deja depose OU trop tard (meme reponse, volontairement) : on s'arrete.
    [InlineData(400, "DejaDeposeOuTropTard")]
    [InlineData(401, "DejaDeposeOuTropTard")]
    [InlineData(404, "DejaDeposeOuTropTard")]
    [InlineData(408, "AReessayer")]
    [InlineData(429, "AReessayer")]
    [InlineData(500, "AReessayer")]
    [InlineData(503, "AReessayer")]
    [InlineData(null, "AReessayer")]
    public void Une_reponse_du_depot_final_dit_s_il_faut_reessayer(int? statut, string attendu)
    {
        Assert.Equal(attendu, LiveContestClientService.IssueDe(statut).ToString());
    }

    [Fact]
    public void Les_nouveaux_essais_tiennent_dans_les_deux_minutes_de_LiveContest()
    {
        var total = LiveContestClientService.AttentesDesEssais.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        // Premier envoi environ 3 s apres la cloture, plus le temps des reponses : il reste une marge confortable.
        Assert.True(total <= TimeSpan.FromSeconds(90), total.ToString());
        Assert.True(LiveContestClientService.AttentesDesEssais.Length >= 2);
    }
}
