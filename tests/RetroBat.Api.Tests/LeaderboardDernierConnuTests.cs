using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le panneau de classement montre le dernier classement connu des l'ouverture (2026-10-03) : il
/// se garde sur disque a chaque reponse du site, se relit apres un redemarrage, et reste affiche
/// quand le site ne repond plus.
/// </summary>
public sealed class LeaderboardDernierConnuTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-derniers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { }
    }

    private const string Board = """
        {"ok":true,"rows":[
          {"player":"Ayumi","value":9000,"city":"Tokyo","country":"JP"},
          {"player":"Nelfe80","value":4500,"city":"Paris","country":"FR"}]}
        """;

    private sealed class FauxSite(Func<HttpResponseMessage> reponse) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Gestionnaire(reponse));

        private sealed class Gestionnaire(Func<HttpResponseMessage> reponse) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(reponse());
        }
    }

    private LeaderboardClient Client(Func<HttpResponseMessage> reponse)
        => new(new FauxSite(reponse), NullLogger<LeaderboardClient>.Instance) { DossierDisque = _dossier };

    [Fact]
    public async Task Garde_sur_disque_et_relit_apres_redemarrage()
    {
        var premier = Client(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Board) });
        var frais = await premier.MondeAsync("metal-slug-3", "Nelfe80", CancellationToken.None, "1cc");
        Assert.Equal(2, frais.Lignes.Count);

        // Une API qui redemarre : memoire vide, la copie disque repond.
        var apres = Client(() => throw new HttpRequestException("pas appele"));
        var connu = apres.DernierConnu("metal-slug-3", "1cc");
        Assert.NotNull(connu);
        Assert.Equal(new[] { "Ayumi", "Nelfe80" }, connu!.Lignes.Select(l => l.Joueur));
        Assert.True(connu.Lignes[1].CestMoi);
        Assert.Null(apres.DernierConnu("metal-slug-3", "1cc-multi"));   // une autre regle, jamais lue
    }

    [Fact]
    public async Task Hors_ligne_le_dernier_connu_reste_affiche()
    {
        var enLigne = Client(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Board) });
        await enLigne.MondeAsync("metal-slug-3", "Nelfe80", CancellationToken.None, "1cc");

        var horsLigne = Client(() => throw new HttpRequestException("site injoignable"));
        var r = await horsLigne.MondeAsync("metal-slug-3", "Nelfe80", CancellationToken.None, "1cc");
        Assert.Equal(LeaderboardClient.EtatHorsLigne, r.Etat);
        Assert.Equal(2, r.Lignes.Count);

        var jamaisVu = await horsLigne.MondeAsync("bubble-bobble", "Nelfe80", CancellationToken.None, "1cc");
        Assert.Empty(jamaisVu.Lignes);
    }
}
