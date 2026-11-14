using System.Net;
using System.Text;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Point 4 du site (14/11) : le panneau des defis lit tous les classements d'un jeu par UN appel
/// <c>challenges/board?rulesets=a,b,c</c>. Un site qui ne le connait pas (400, 404, reponse sans <c>boards</c>) fait revenir
/// a un appel par epreuve ; une panne n'est pas un site ancien ; l'empreinte et le 304 restent.
/// </summary>
public sealed class ClassementsDesDefisGroupesTests
{
    private static string Tableau(string joueur, long valeur)
        => "{\"total\":1,\"rows\":[{\"player\":\"" + joueur + "\",\"value\":" + valeur + "}]}";

    /// <summary>Un faux site : le nouveau (rulesets), l'ancien (404 sans ruleset), ou une reponse imposee a la lecture groupee.</summary>
    private sealed class Site : HttpMessageHandler
    {
        public bool Nouveau = true;
        public HttpStatusCode? StatutDuGroupe;
        public string? CorpsDuGroupe;
        public readonly List<string> Adresses = [];
        public readonly List<string?> Empreintes = [];

        private static string? Parametre(Uri adresse, string nom)
        {
            foreach (var morceau in adresse.Query.TrimStart('?').Split('&'))
            {
                var egal = morceau.IndexOf('=');
                if (egal > 0 && morceau[..egal] == nom) return Uri.UnescapeDataString(morceau[(egal + 1)..]);
            }

            return null;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var adresse = request.RequestUri!;
            lock (Adresses)
            {
                Adresses.Add(adresse.ToString());
                Empreintes.Add(request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null);
            }

            if (Parametre(adresse, "rulesets") is { } liste)
            {
                if (StatutDuGroupe is { } statut)
                {
                    return Task.FromResult(new HttpResponseMessage(statut) { Content = new StringContent(CorpsDuGroupe ?? "{}") });
                }

                if (!Nouveau)
                {
                    // Le site d'avant : `ruleset` absent, epreuve inconnue.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"ok\":false,\"error\":\"challenge_not_found\"}"),
                    });
                }

                if (request.Headers.Contains("If-None-Match")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                var regles = liste.Split(',');
                var boards = string.Join(",", regles.Where(r => r != "inconnue").Select(r => "\"" + r + "\":" + Tableau("p-" + r, 100)));
                var manquantes = string.Join(",", regles.Where(r => r == "inconnue").Select(r => "\"" + r + "\""));
                var reponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true,\"boards\":{" + boards + "},\"missing\":[" + manquantes + "]}",
                        Encoding.UTF8, "application/json"),
                };
                reponse.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"g1\"");
                return Task.FromResult(reponse);
            }

            var regle = Parametre(adresse, "ruleset") ?? "";
            if (regle is "" or "inconnue")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true," + Tableau("p-" + regle, 200)[1..], Encoding.UTF8, "application/json"),
            });
        }

        public int Groupees => Adresses.Count(a => a.Contains("rulesets=", StringComparison.Ordinal));

        public int Seules => Adresses.Count(a => a.Contains("ruleset=", StringComparison.Ordinal) && !a.Contains("rulesets=", StringComparison.Ordinal));
    }

    private sealed class Fabrique(Site site) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(site, disposeHandler: false);
    }

    private static ClassementsDesDefis Nouveaux(Site site) => new(new Fabrique(site)) { Lectures = new LecturesConditionnelles() };

    [Fact]
    public async Task Le_nouveau_site_rend_tous_les_classements_en_un_appel()
    {
        var site = new Site();
        var classements = Nouveaux(site);

        var lus = await classements.ChargerAsync(["a", "b", "c", "inconnue"], CancellationToken.None);

        Assert.Single(site.Adresses);
        Assert.Contains("rulesets=a,b,c,inconnue&limit=100", site.Adresses[0], StringComparison.Ordinal);
        Assert.Equal(3, lus.Count);
        Assert.Equal("p-b", lus["b"].Lignes[0].Joueur);
        Assert.Equal(100, lus["c"].Lignes[0].Valeur);
        Assert.False(lus.ContainsKey("inconnue"));
    }

    [Fact]
    public async Task Le_site_d_avant_fait_revenir_a_un_appel_par_epreuve_et_n_est_pas_redemande_tout_de_suite()
    {
        var site = new Site { Nouveau = false };
        var classements = Nouveaux(site);

        var lus = await classements.ChargerAsync(["a", "b", "c"], CancellationToken.None);
        Assert.Equal(3, lus.Count);
        Assert.Equal(200, lus["a"].Lignes[0].Valeur);
        Assert.Equal(1, site.Groupees);
        Assert.Equal(3, site.Seules);

        // Relu (verdict) : plus d'essai groupe pendant une heure.
        await classements.ChargerAsync(["a", "b", "c"], CancellationToken.None, forcer: true);
        Assert.Equal(1, site.Groupees);
        Assert.Equal(6, site.Seules);
        Assert.False(classements.LectureGroupeePossible(DateTime.UtcNow));
        Assert.True(classements.LectureGroupeePossible(DateTime.UtcNow + ClassementsDesDefis.OubliDuSiteAncien + TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"ok\":false,\"error\":\"bad_rulesets\"}")]
    [InlineData(HttpStatusCode.OK, "{\"ok\":true,\"total\":0,\"rows\":[]}")]
    public async Task Un_400_ou_une_reponse_sans_boards_fait_revenir_a_un_appel_par_epreuve(HttpStatusCode statut, string corps)
    {
        var site = new Site { StatutDuGroupe = statut, CorpsDuGroupe = corps };
        var classements = Nouveaux(site);

        var lus = await classements.ChargerAsync(["a", "b"], CancellationToken.None);

        Assert.Equal(2, lus.Count);
        Assert.Equal(1, site.Groupees);
        Assert.Equal(2, site.Seules);
    }

    [Fact]
    public async Task Une_panne_du_site_n_est_pas_un_site_ancien()
    {
        var site = new Site { StatutDuGroupe = HttpStatusCode.ServiceUnavailable };
        var classements = Nouveaux(site);

        var lus = await classements.ChargerAsync(["a", "b"], CancellationToken.None);

        Assert.Empty(lus);
        Assert.Single(site.Adresses);
        Assert.True(classements.LectureGroupeePossible(DateTime.UtcNow));
    }

    [Fact]
    public async Task La_relecture_groupee_renvoie_l_empreinte_et_garde_les_classements_sur_un_304()
    {
        var site = new Site();
        var classements = Nouveaux(site);

        await classements.ChargerAsync(["a", "b"], CancellationToken.None);
        var lus = await classements.ChargerAsync(["a", "b"], CancellationToken.None, forcer: true);

        Assert.Equal(2, site.Adresses.Count);
        Assert.Null(site.Empreintes[0]);
        Assert.Equal("\"g1\"", site.Empreintes[1]);
        Assert.Equal(2, lus.Count);
        Assert.Equal("p-a", lus["a"].Lignes[0].Joueur);
    }

    [Fact]
    public async Task Seules_les_epreuves_pas_a_jour_sont_demandees_et_par_paquets_de_64()
    {
        var site = new Site();
        var classements = Nouveaux(site);
        await classements.ChargerAsync(["a"], CancellationToken.None);
        Assert.Equal(1, site.Seules);

        var regles = Enumerable.Range(0, 70).Select(i => "r" + i).Append("a").ToList();
        var lus = await classements.ChargerAsync(regles, CancellationToken.None);

        Assert.Equal(71, lus.Count);
        Assert.Equal(2, site.Groupees);
        Assert.Equal(1, site.Seules);
        Assert.DoesNotContain(site.Adresses.Where(u => u.Contains("rulesets=", StringComparison.Ordinal)),
            u => u.Contains(",a&", StringComparison.Ordinal));
    }

    [Fact]
    public void La_lecture_groupee_se_lit_comme_la_lecture_seule()
    {
        var lus = ClassementsDesDefis.LireGroupe("{\"ok\":true,\"boards\":{\"x\":{\"total\":7,\"rows\":[{\"player\":\"q\",\"value\":5}]}},\"missing\":[]}");
        Assert.NotNull(lus);
        Assert.Equal(7, lus!["x"].Total);
        Assert.Null(ClassementsDesDefis.LireGroupe("{\"ok\":true,\"total\":0,\"rows\":[]}"));
        Assert.Empty(ClassementsDesDefis.LireGroupe("{\"ok\":true,\"boards\":{},\"missing\":[\"a\"]}")!);
    }
}
