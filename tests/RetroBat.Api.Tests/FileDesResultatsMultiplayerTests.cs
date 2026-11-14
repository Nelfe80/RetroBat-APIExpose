using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Brief du 13/11, MULTI-05 : la fin, le rapport et l'annulation d'une rencontre survivent a un site muet.</summary>
public class FileDesResultatsMultiplayerTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-mp-resultats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    /// <summary>Le site : rend les statuts prevus dans l'ordre (null = injoignable), et garde les corps recus.</summary>
    private sealed class Site : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode?> _reponses;
        public List<(string Chemin, string Corps)> Recus { get; } = new();
        public Site(params HttpStatusCode?[] reponses) => _reponses = new Queue<HttpStatusCode?>(reponses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var corps = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Recus.Add((request.RequestUri!.AbsolutePath, corps));
            var statut = _reponses.Count > 0 ? _reponses.Dequeue() : HttpStatusCode.OK;
            if (statut is null) throw new HttpRequestException("site injoignable");
            var texte = statut == HttpStatusCode.ServiceUnavailable ? "<html>maintenance</html>" : "{\"ok\":true}";
            return new HttpResponseMessage(statut.Value) { Content = new StringContent(texte) };
        }
    }

    private sealed class SansFabrique : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("jamais le reseau dans un test");
    }

    private (MultiplayerClient Client, FileDesResultatsMultiplayer File, Func<DateTime> Horloge, Action<TimeSpan> Avancer) Monter(Site site)
    {
        var maintenant = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
        var file = new FileDesResultatsMultiplayer(_dossier, () => maintenant, new Random(7));
        var client = new MultiplayerClient(new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance),
            clientDeTest: () => new HttpClient(site) { BaseAddress = new Uri("https://nelfeplay.test") }, resultats: file);
        return (client, file, () => maintenant, d => maintenant += d);
    }

    [Fact]
    public async Task Fin_de_partie_sans_reponse_du_site_gardee_puis_renvoyee_a_l_identique()
    {
        var site = new Site(null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var (client, file, _, avancer) = Monter(site);
        var reprise = new RepriseDesResultatsMultiplayer(file, client);

        var reponse = await client.TerminerAsync("0123456789abcdef0123456789abcdef", "match_over", 312, CancellationToken.None, placeGagnante: 2,
            rangs: new[] { new MultiplayerRang(1, 2, null), new MultiplayerRang(2, 1, null) });
        Assert.Null(reponse);
        Assert.Single(file.EnAttente());

        // Pas encore du : rien ne part.
        Assert.Equal(0, await reprise.UnTourAsync(CancellationToken.None));
        Assert.Single(site.Recus);

        avancer(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await reprise.UnTourAsync(CancellationToken.None));   // 503 du site en maintenance : garde
        avancer(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await reprise.UnTourAsync(CancellationToken.None));   // le site repond
        Assert.Empty(file.EnAttente());

        Assert.Equal(3, site.Recus.Count);
        Assert.All(site.Recus, r => Assert.Equal("/api/v1/agent/multiplayer/ended", r.Chemin));
        Assert.Equal(JsonNode.Parse(site.Recus[0].Corps)!.ToJsonString(), JsonNode.Parse(site.Recus[2].Corps)!.ToJsonString());
        var corps = JsonNode.Parse(site.Recus[2].Corps)!;
        Assert.Equal(2, (int)corps["winner_seat"]!);
        Assert.Equal(312, (int)corps["duration_s"]!);
    }

    [Fact]
    public async Task Une_reponse_du_site_meme_un_refus_n_est_pas_gardee()
    {
        var site = new Site(HttpStatusCode.Conflict, HttpStatusCode.OK);
        var (client, file, _, _) = Monter(site);
        await client.RapporterAsync("0123456789abcdef0123456789abcdef", 1, CancellationToken.None);
        await client.AnnulerAsync("0123456789abcdef0123456789abcdef", "load_state", CancellationToken.None);
        Assert.Empty(file.EnAttente());
    }

    [Fact]
    public async Task Rapport_et_annulation_d_un_invite_renvoyes_dans_l_ordre_au_redemarrage()
    {
        var site = new Site(null, null);
        var (client, file, _, avancer) = Monter(site);
        await client.RapporterAsync("0123456789abcdef0123456789abcdef", 1, CancellationToken.None);
        avancer(TimeSpan.FromMilliseconds(5));
        await client.AnnulerAsync("0123456789abcdef0123456789abcdef", "load_state", CancellationToken.None);
        Assert.Equal(2, file.EnAttente().Count);

        // L'API redemarre : une file neuve sur le meme dossier, l'espacement est oublie, tout part tout de suite.
        var siteRevenu = new Site();
        var clientRevenu = new MultiplayerClient(new SansFabrique(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance),
            clientDeTest: () => new HttpClient(siteRevenu) { BaseAddress = new Uri("https://nelfeplay.test") });
        var fileRevenue = new FileDesResultatsMultiplayer(_dossier);
        Assert.Equal(2, await new RepriseDesResultatsMultiplayer(fileRevenue, clientRevenu).UnTourAsync(CancellationToken.None));
        Assert.Equal(2, siteRevenu.Recus.Count);
        Assert.Null(JsonNode.Parse(siteRevenu.Recus[0].Corps)!["voided"]);
        Assert.True((bool)JsonNode.Parse(siteRevenu.Recus[1].Corps)!["voided"]!);
    }

    [Fact]
    public async Task Apres_24_h_sans_reponse_l_envoi_est_abandonne()
    {
        var site = new Site(null, null, null, null);
        var (client, file, _, avancer) = Monter(site);
        await client.TerminerAsync("0123456789abcdef0123456789abcdef", "game_closed", 60, CancellationToken.None);
        avancer(FileDesResultatsMultiplayer.DureeMax + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await new RepriseDesResultatsMultiplayer(file, client).UnTourAsync(CancellationToken.None));
        Assert.Empty(file.EnAttente());
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(4, 40)]
    [InlineData(10, 300)]
    public void Delai_double_a_chaque_echec_jusqu_a_5_minutes(int essais, double secondes)
    {
        Assert.Equal(secondes, FileDesResultatsMultiplayer.Delai(essais, 0.5).TotalSeconds, 3);
    }
}
