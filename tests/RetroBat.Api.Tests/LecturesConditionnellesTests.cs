using System.Net;
using System.Text;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-11 (14/11) : le site rend une empreinte sur les classements, puis 304 sans corps a qui la renvoie. La borne
/// la renvoie et resert le corps garde.
/// </summary>
public sealed class LecturesConditionnellesTests
{
    private sealed class Site : HttpMessageHandler
    {
        public string Corps { get; set; } = "{\"rows\":[]}";
        public string? Empreinte { get; set; } = "\"abc\"";
        public HttpStatusCode? Force { get; set; }
        public List<string?> Recues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var renvoyee = request.Headers.TryGetValues("If-None-Match", out var v) ? string.Join(",", v) : null;
            Recues.Add(renvoyee);
            if (Force is { } code) return Task.FromResult(new HttpResponseMessage(code));
            var reponse = renvoyee is not null && renvoyee == Empreinte
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Corps, Encoding.UTF8, "application/json") };
            if (Empreinte is not null) reponse.Headers.TryAddWithoutValidation("ETag", Empreinte);
            return Task.FromResult(reponse);
        }
    }

    private static HttpClient Client(Site site) => new(site) { BaseAddress = new Uri("https://nelfeplay.test") };

    [Fact]
    public async Task La_seconde_lecture_renvoie_l_empreinte_et_resert_le_corps_sur_304()
    {
        var site = new Site { Corps = "{\"rows\":[1]}" };
        var lectures = new LecturesConditionnelles();

        Assert.Equal("{\"rows\":[1]}", await lectures.LireAsync(Client(site), "/api/v1/scores/board?game=a", CancellationToken.None));
        Assert.Equal("{\"rows\":[1]}", await lectures.LireAsync(Client(site), "/api/v1/scores/board?game=a", CancellationToken.None));

        Assert.Equal([null, "\"abc\""], site.Recues);
    }

    [Fact]
    public async Task Un_classement_change_remplace_le_corps_garde()
    {
        var site = new Site { Corps = "v1" };
        var lectures = new LecturesConditionnelles();
        await lectures.LireAsync(Client(site), "/a", CancellationToken.None);

        site.Corps = "v2";
        site.Empreinte = "\"def\"";

        Assert.Equal("v2", await lectures.LireAsync(Client(site), "/a", CancellationToken.None));
        Assert.Equal("v2", await lectures.LireAsync(Client(site), "/a", CancellationToken.None));
        Assert.Equal("\"def\"", site.Recues[^1]);
    }

    [Fact]
    public async Task Une_adresse_sans_empreinte_ne_garde_rien()
    {
        var site = new Site { Empreinte = null };
        var lectures = new LecturesConditionnelles();

        await lectures.LireAsync(Client(site), "/a", CancellationToken.None);
        await lectures.LireAsync(Client(site), "/a", CancellationToken.None);

        Assert.All(site.Recues, r => Assert.Null(r));
        Assert.Equal(0, lectures.Nombre);
    }

    [Fact]
    public async Task Une_erreur_du_site_leve_comme_avant()
    {
        var site = new Site { Force = HttpStatusCode.ServiceUnavailable };
        var lectures = new LecturesConditionnelles();

        await Assert.ThrowsAsync<HttpRequestException>(() => lectures.LireAsync(Client(site), "/a", CancellationToken.None));
    }

    [Fact]
    public async Task Au_dela_du_plafond_la_plus_ancienne_adresse_s_oublie()
    {
        var site = new Site();
        var lectures = new LecturesConditionnelles(plafond: 2);

        await lectures.LireAsync(Client(site), "/a", CancellationToken.None);
        await lectures.LireAsync(Client(site), "/b", CancellationToken.None);
        await lectures.LireAsync(Client(site), "/a", CancellationToken.None);   // /a resservie : la plus recente
        await lectures.LireAsync(Client(site), "/c", CancellationToken.None);   // /b s'oublie

        Assert.Equal(2, lectures.Nombre);
        site.Recues.Clear();
        await lectures.LireAsync(Client(site), "/b", CancellationToken.None);
        Assert.Null(site.Recues[0]);
    }
}
