using System.Net;
using System.Text;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-10 (14/11) : le panneau des defis lit le classement de chaque epreuve du jeu a chaque ouverture. Pour lui, un
/// classement lu il y a moins de 5 min ne se relit pas ; le Deck et le direct gardent 60 s ; un verdict force la relecture.
/// </summary>
public sealed class ClassementsDesDefisCacheTests
{
    private sealed class Site : HttpMessageHandler
    {
        public int Appels;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Appels);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"total\":1,\"rows\":[{\"player\":\"p\",\"value\":100}]}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Fabrique(Site site) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(site, disposeHandler: false);
    }

    [Fact]
    public async Task Le_panneau_ne_relit_pas_un_classement_de_moins_de_cinq_minutes()
    {
        var site = new Site();
        var classements = new ClassementsDesDefis(new Fabrique(site)) { Lectures = new LecturesConditionnelles() };
        string[] regles = ["a", "b", "c"];

        await classements.ChargerAsync(regles, CancellationToken.None, fraicheur: ClassementsDesDefis.FraicheurDuPanneau);
        await classements.ChargerAsync(regles, CancellationToken.None, fraicheur: ClassementsDesDefis.FraicheurDuPanneau);

        Assert.Equal(3, site.Appels);
        Assert.True(classements.AJour("a", ClassementsDesDefis.FraicheurDuPanneau, DateTime.UtcNow.AddMinutes(4)));
        Assert.False(classements.AJour("a", ClassementsDesDefis.FraicheurDuPanneau, DateTime.UtcNow.AddMinutes(6)));
        // Le Deck et le direct : 60 s.
        Assert.False(classements.AJour("a", TimeSpan.FromSeconds(60), DateTime.UtcNow.AddSeconds(61)));
    }

    [Fact]
    public async Task Un_verdict_force_la_relecture()
    {
        var site = new Site();
        var classements = new ClassementsDesDefis(new Fabrique(site)) { Lectures = new LecturesConditionnelles() };

        await classements.ChargerAsync(["a"], CancellationToken.None, fraicheur: ClassementsDesDefis.FraicheurDuPanneau);
        await classements.ChargerAsync(["a"], CancellationToken.None, forcer: true);

        Assert.Equal(2, site.Appels);
    }
}
