using System.Net;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La pastille devant le pseudo du panneau (2026-10-04) : verte quand le dernier echange avec
/// NelfePlay a abouti, rouge sinon. Ces tests gelent ce qui compte comme un echange abouti.
/// </summary>
public sealed class LiaisonNelfePlayTests
{
    private sealed class Faux(Func<HttpRequestMessage, HttpResponseMessage> reponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reponse(request));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> reponse) =>
        new(new LiaisonNelfePlayHandler(() => "https://nelfeplay.com") { InnerHandler = new Faux(reponse) });

    [Fact]
    public async Task La_pastille_suit_le_dernier_echange_avec_le_site()
    {
        LiaisonNelfePlay.Oublier();
        Assert.False(LiaisonNelfePlay.EnLigne);   // aucun echange encore : rouge

        using (var c = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            await c.GetAsync("https://nelfeplay.com/api/v1/scores/board?game=1942");
        Assert.True(LiaisonNelfePlay.EnLigne);

        // Un refus est une reponse : le site est la.
        using (var c = Client(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)))
            await c.GetAsync("https://nelfeplay.com/api/v1/agent/scores/submissions");
        Assert.True(LiaisonNelfePlay.EnLigne);

        using (var c = Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)))
            await c.GetAsync("https://nelfeplay.com/api/v1/agent/work");
        Assert.False(LiaisonNelfePlay.EnLigne);

        using (var c = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            await c.GetAsync("https://NelfePlay.com/api/v1/agent/work");
        Assert.True(LiaisonNelfePlay.EnLigne);

        // Connexion refusee : rouge, et l'erreur remonte telle quelle a l'appelant.
        using (var c = Client(_ => throw new HttpRequestException("refusee")))
            await Assert.ThrowsAsync<HttpRequestException>(() => c.GetAsync("https://nelfeplay.com/api/v1/agent/work"));
        Assert.False(LiaisonNelfePlay.EnLigne);
        Assert.NotNull(LiaisonNelfePlay.DernierEchangeUtc);
    }

    [Fact]
    public async Task Un_autre_serveur_ne_change_pas_la_pastille()
    {
        // ES (127.0.0.1:1234), un pair LAN, un miroir de replays : rien de tout cela n'est NelfePlay.
        var handler = new LiaisonNelfePlayHandler(() => "http://127.0.0.1:9")
        {
            InnerHandler = new Faux(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        using var c = new HttpClient(handler);
        LiaisonNelfePlay.Noter(true);
        await c.GetAsync("http://127.0.0.1:1234/runningGame");
        await c.GetAsync("https://github.com/Nelfe80");
        Assert.True(LiaisonNelfePlay.EnLigne);
    }

    [Theory]
    [InlineData("https://nelfeplay.com/api/v1/x", "https://nelfeplay.com", true)]
    [InlineData("https://nelfeplay.com:443/x", "https://nelfeplay.com/", true)]
    [InlineData("http://nelfeplay.com/x", "https://nelfeplay.com", false)]
    [InlineData("http://127.0.0.1:1234/x", "http://127.0.0.1:9", false)]
    [InlineData("http://127.0.0.1:9/api", "http://127.0.0.1:9", true)]
    [InlineData("https://nelfeplay.com/x", "pas une adresse", false)]
    public void L_adresse_de_NelfePlay_se_reconnait_a_son_schema_son_hote_et_son_port(string adresse, string site, bool attendu)
    {
        Assert.Equal(attendu, LiaisonNelfePlay.EstNelfePlay(new Uri(adresse), site));
    }
}
