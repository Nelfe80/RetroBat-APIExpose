using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Points 5 et 6 du site (14/11) : <c>agent/work</c> porte les directs suivis, les avatars, la reconstruction et la boite du
/// relais ; <c>agent/whats-new</c> remplace sept minuteurs. Avec un site d'avant (champs absents, 404), tout tourne comme
/// avant ; avec le nouveau, chaque minuteur couvert se tait, et seul ce dont la revision change se relit.
/// </summary>
public sealed class QuoiDeNeufTests
{
    private static JsonElement Json(string texte)
    {
        using var document = JsonDocument.Parse(texte);
        return document.RootElement.Clone();
    }

    // ---- Le point de rencontre ----

    [Fact]
    public async Task Un_champ_traite_couvre_son_minuteur_un_champ_absent_ou_en_erreur_non()
    {
        var suivi = new SuiviDuSite();
        var recus = new List<string>();
        suivi.Traiter(SuiviDuSite.DirectsSuivis, (liste, _) => { recus.Add(liste.GetRawText()); return Task.CompletedTask; });
        suivi.Traiter(SuiviDuSite.AvatarsEnAttente, (_, _) => throw new InvalidOperationException("planche"));
        // Pas de traitement inscrit pour la reconstruction : le minuteur reste.

        var traites = await suivi.DistribuerAsync(Json("{\"ok\":true,\"live_following\":[],\"avatar_pending\":[],\"recovery\":{\"contribute\":false}}"),
            TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.Equal([SuiviDuSite.DirectsSuivis], traites);
        Assert.Equal(["[]"], recus);
        var maintenant = DateTime.UtcNow;
        Assert.True(suivi.Couvert(SuiviDuSite.DirectsSuivis, maintenant));
        Assert.False(suivi.Couvert(SuiviDuSite.AvatarsEnAttente, maintenant));
        Assert.False(suivi.Couvert(SuiviDuSite.Reconstruction, maintenant));
        Assert.False(suivi.Couvert(SuiviDuSite.BoiteDuRelais, maintenant));
        Assert.False(suivi.Couvert(SuiviDuSite.DirectsSuivis, maintenant.AddMinutes(11)));
    }

    [Fact]
    public async Task Le_releve_d_un_site_d_avant_ne_couvre_rien()
    {
        var suivi = new SuiviDuSite();
        var appels = 0;
        foreach (var champ in SuiviDuSite.ChampsJoints) suivi.Traiter(champ, (_, _) => { appels++; return Task.CompletedTask; });

        var traites = await suivi.DistribuerAsync(Json("{\"ok\":true,\"player_code\":\"X\",\"intents\":[],\"messages\":[]}"),
            TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.Empty(traites);
        Assert.Equal(0, appels);
        Assert.All(SuiviDuSite.ChampsJoints, champ => Assert.False(suivi.Couvert(champ, DateTime.UtcNow)));
    }

    [Fact]
    public void La_couverture_vaut_deux_releves_et_demi_et_jamais_moins_de_cinq_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(750), SuiviDuSite.Validite(TimeSpan.FromSeconds(300)));
        Assert.Equal(TimeSpan.FromMinutes(5), SuiviDuSite.Validite(TimeSpan.FromSeconds(60)));
    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("W/\"abc\"", "abc")]
    [InlineData("abc", "abc")]
    [InlineData(null, "")]
    public void Une_empreinte_se_compare_sans_guillemets(string? empreinte, string attendue)
        => Assert.Equal(attendue, SuiviDuSite.Normaliser(empreinte));

    [Fact]
    public async Task Une_revision_fait_foi_tant_que_le_quoi_de_neuf_repond()
    {
        var suivi = new SuiviDuSite();
        var vues = new List<string>();
        suivi.SurRevision(SuiviDuSite.JeuxOuverts, (r, _) => { vues.Add(r); return Task.CompletedTask; });

        Assert.False(suivi.RevisionAJour(SuiviDuSite.JeuxOuverts, "\"r1\"", DateTime.UtcNow));
        await suivi.NoterLesRevisionsAsync(new Dictionary<string, string> { [SuiviDuSite.JeuxOuverts] = "r1" }, TimeSpan.FromMinutes(10),
            CancellationToken.None);

        Assert.Equal(["r1"], vues);
        Assert.True(suivi.QuoiDeNeufActif(DateTime.UtcNow));
        Assert.True(suivi.RevisionAJour(SuiviDuSite.JeuxOuverts, "\"r1\"", DateTime.UtcNow));
        Assert.False(suivi.RevisionAJour(SuiviDuSite.JeuxOuverts, "\"r0\"", DateTime.UtcNow));
        Assert.False(suivi.RevisionAJour(SuiviDuSite.JeuxOuverts, "\"r1\"", DateTime.UtcNow.AddMinutes(11)));

        suivi.OublierLeQuoiDeNeuf();
        Assert.False(suivi.RevisionAJour(SuiviDuSite.JeuxOuverts, "\"r1\"", DateTime.UtcNow));
    }

    // ---- Point 5 : le releve agent/work ----

    [Fact]
    public void Le_releve_donne_le_curseur_de_la_boite_s_il_y_en_a_un()
    {
        Assert.Equal("/api/v1/agent/work", NelfePlayAgentService.AdresseDuReleve(null));
        Assert.Equal("/api/v1/agent/work?inbox_since=42", NelfePlayAgentService.AdresseDuReleve(42));
        Assert.Equal("/api/v1/agent/work?inbox_since=0", NelfePlayAgentService.AdresseDuReleve(-3));
    }

    [Fact]
    public void Le_releve_periodique_ne_se_tait_que_si_le_travail_est_suivi_et_relu_dans_l_heure()
    {
        var maintenant = DateTime.UtcNow;
        Assert.False(NelfePlayAgentService.ReleveCouvert(false, maintenant.AddMinutes(-1), maintenant));
        Assert.False(NelfePlayAgentService.ReleveCouvert(true, null, maintenant));
        Assert.True(NelfePlayAgentService.ReleveCouvert(true, maintenant.AddMinutes(-59), maintenant));
        Assert.False(NelfePlayAgentService.ReleveCouvert(true, maintenant.AddMinutes(-61), maintenant));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, true, false, false, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(true, false, false, false, true, false)]
    public void La_boite_du_relais_ne_se_tait_qu_au_repos(bool couvert, bool spectateur, bool salle, bool planches, bool replay, bool attendu)
        => Assert.Equal(attendu, ReplayRelayService.ReleveCouvert(couvert, spectateur, salle, planches, replay));

    [Fact]
    public void La_reconstruction_ne_se_tait_ni_armee_ni_signalee()
    {
        Assert.True(NelfePlayScoringReporter.RecoveryCouvert(true, false, null));
        Assert.True(NelfePlayScoringReporter.RecoveryCouvert(true, false, NelfePlayScoringReporter.IssueDuRecovery.Calme));
        Assert.False(NelfePlayScoringReporter.RecoveryCouvert(false, false, null));
        Assert.False(NelfePlayScoringReporter.RecoveryCouvert(true, true, null));
        Assert.False(NelfePlayScoringReporter.RecoveryCouvert(true, false, NelfePlayScoringReporter.IssueDuRecovery.Armee));
    }

    [Fact]
    public async Task Une_reconstruction_armee_jointe_au_releve_fait_relire_le_battement()
    {
        var suivi = new SuiviDuSite();
        var reporter = new NelfePlayScoringReporter(new SimpleEventBus(), new SansReseau(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance),
            suivi: suivi);

        await suivi.DistribuerAsync(Json("{\"recovery\":{\"contribute\":false,\"epoch\":\"\",\"since\":null,\"armed_at\":null}}"),
            TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.False(reporter.ReconstructionSignalee);
        Assert.True(suivi.Couvert(SuiviDuSite.Reconstruction, DateTime.UtcNow));

        await suivi.DistribuerAsync(Json("{\"recovery\":{\"contribute\":true,\"epoch\":\"e1\",\"since\":\"2026-11-14T00:00:00Z\",\"armed_at\":null}}"),
            TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.True(reporter.ReconstructionSignalee);
    }

    [Fact]
    public void Les_directs_suivis_s_annoncent_une_fois_et_pas_a_la_premiere_liste()
    {
        static LeaderboardSocialClient.Direct D(string session) => new(session, "h", "joueur", "jeu", "Jeu", "nes", "", "", "");
        var guet = new GuetDesDirectsSuivis();

        Assert.Empty(guet.Nouveaux([D("a")]));
        Assert.Equal(["b"], guet.Nouveaux([D("a"), D("b")]).Select(d => d.Session));
        Assert.Empty(guet.Nouveaux([D("a"), D("b")]));
        Assert.Empty(guet.Nouveaux([D("b")]));
        // « a » a repris : il se reannonce.
        Assert.Equal(["a"], guet.Nouveaux([D("a"), D("b")]).Select(d => d.Session));
    }

    [Fact]
    public void La_liste_jointe_se_lit_comme_celle_de_live_following()
    {
        var directs = LeaderboardSocialClient.LireDirects(Json(
            "[{\"session\":\"s1\",\"handle\":\"h1\",\"player\":\"P1\",\"game\":\"g\",\"game_name\":\"G\",\"system\":\"nes\"},{\"handle\":\"sans-session\"},3]"));
        Assert.Single(directs);
        Assert.Equal("P1", directs[0].Pseudo);
        Assert.Empty(LeaderboardSocialClient.LireDirects(Json("{\"live\":[]}")));
    }

    // ---- Point 6 : agent/whats-new ----

    private sealed class SansReseau : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SiteMuet());
    }

    private sealed class SiteMuet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("pas de reseau dans les tests");
    }

    /// <summary>Un faux site : le nouveau (quoi de neuf, 304 sur l'empreinte), l'ancien (404), ou en panne.</summary>
    private sealed class Site : HttpMessageHandler
    {
        public HttpStatusCode? Impose;
        public string Travail = "w1";
        public string JeuxOuverts = "o1";
        public string Defis = "c1";
        public int? PollAfter = 300;
        public readonly List<string> Adresses = [];
        public int NonModifies;

        public string Corps()
        {
            var poll = PollAfter is { } p ? ",\"poll_after\":" + p : "";
            return "{\"ok\":true,\"work\":{\"revision\":\"" + Travail + "\"},\"open_games\":{\"revision\":\"" + JeuxOuverts
                   + "\"},\"challenges_open\":{\"revision\":\"" + Defis + "\"},\"live_following\":[],\"avatar_pending\":[],"
                   + "\"recovery\":{\"contribute\":false,\"epoch\":\"\",\"since\":null,\"armed_at\":null},"
                   + "\"relay_inbox\":{\"ok\":true,\"next\":0,\"requests\":[]}" + poll + "}";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Adresses.Add(request.RequestUri!.PathAndQuery);
            if (Impose is { } statut) return Task.FromResult(new HttpResponseMessage(statut) { Content = new StringContent("{\"error\":\"x\"}") });
            var corps = Corps();
            var empreinte = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(corps)))[..16] + "\"";
            if (request.Headers.TryGetValues("If-None-Match", out var v) && v.First() == empreinte)
            {
                NonModifies++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var reponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
            reponse.Headers.TryAddWithoutValidation("ETag", empreinte);
            return Task.FromResult(reponse);
        }
    }

    private sealed class Banc
    {
        public readonly Site Site = new();
        public readonly SuiviDuSite Suivi = new();
        public int Releves;
        public bool ReleveAboutit = true;
        public string? Credential = "cred";
        public bool Appairee = true;
        public readonly List<string> RevisionsVues = [];
        public int Directs;

        public QuoiDeNeufService Service()
        {
            Suivi.CurseurDeLaBoite = () => 7;
            Suivi.Traiter(SuiviDuSite.DirectsSuivis, (_, _) => { Directs++; return Task.CompletedTask; });
            Suivi.SurRevision(SuiviDuSite.JeuxOuverts, (r, _) => { RevisionsVues.Add("open:" + r); return Task.CompletedTask; });
            Suivi.SurRevision(SuiviDuSite.DefisOuverts, (r, _) => { RevisionsVues.Add("defis:" + r); return Task.CompletedTask; });
            return new QuoiDeNeufService(Suivi, () => Credential,
                _ => new HttpClient(Site, disposeHandler: false) { BaseAddress = new Uri("https://site.invalid") },
                _ => { Releves++; return Task.FromResult(ReleveAboutit); }, () => Appairee);
        }
    }

    [Fact]
    public async Task Le_site_d_avant_laisse_tourner_les_minuteurs()
    {
        var banc = new Banc();
        banc.Site.Impose = HttpStatusCode.NotFound;
        var service = banc.Service();

        Assert.Equal(QuoiDeNeufService.Issue.SiteAncien, await service.TourAsync(CancellationToken.None));

        Assert.Equal(0, banc.Releves);
        Assert.Equal(0, banc.Directs);
        Assert.False(banc.Suivi.QuoiDeNeufActif(DateTime.UtcNow));
        Assert.Null(banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));
        Assert.All(SuiviDuSite.ChampsJoints, champ => Assert.False(banc.Suivi.Couvert(champ, DateTime.UtcNow)));
        // Redemande une fois par heure (plus ou moins 10 %).
        var attente = QuoiDeNeufService.Attente(QuoiDeNeufService.Issue.SiteAncien, service.Cadence, 0, 0.5);
        Assert.Equal(TimeSpan.FromHours(1), attente);
    }

    [Fact]
    public async Task Le_nouveau_site_releve_une_fois_puis_ne_relit_que_ce_qui_change()
    {
        var banc = new Banc();
        var service = banc.Service();

        Assert.Equal(QuoiDeNeufService.Issue.Lu, await service.TourAsync(CancellationToken.None));
        Assert.Equal("/api/v1/agent/whats-new?inbox_since=7", banc.Site.Adresses[0]);
        Assert.Equal(1, banc.Releves);
        Assert.Equal(["open:o1", "defis:c1"], banc.RevisionsVues);
        Assert.Equal(1, banc.Directs);
        Assert.Equal("w1", banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));
        Assert.True(banc.Suivi.RevisionAJour(SuiviDuSite.JeuxOuverts, "\"o1\"", DateTime.UtcNow));
        Assert.True(banc.Suivi.Couvert(SuiviDuSite.DirectsSuivis, DateTime.UtcNow));

        // Rien de change : 304, aucun releve de plus, la couverture se prolonge.
        Assert.Equal(QuoiDeNeufService.Issue.Lu, await service.TourAsync(CancellationToken.None));
        Assert.Equal(1, banc.Site.NonModifies);
        Assert.Equal(1, banc.Releves);
        Assert.Equal("w1", banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));

        // Le travail change (une installation, un message) : un releve.
        banc.Site.Travail = "w2";
        await service.TourAsync(CancellationToken.None);
        Assert.Equal(2, banc.Releves);
        Assert.Equal("w2", banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));
        Assert.Equal(3, banc.Site.Adresses.Count);
    }

    [Fact]
    public async Task Un_releve_rate_n_est_pas_suivi_et_se_redemande()
    {
        var banc = new Banc { ReleveAboutit = false };
        var service = banc.Service();

        await service.TourAsync(CancellationToken.None);
        Assert.Equal(1, banc.Releves);
        // Le releve periodique de l'agent ne se tait pas.
        Assert.Null(banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));

        banc.ReleveAboutit = true;
        await service.TourAsync(CancellationToken.None);
        Assert.Equal(2, banc.Releves);
        Assert.Equal("w1", banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));
    }

    [Fact]
    public async Task Une_installation_anonyme_ne_releve_rien_et_ne_donne_pas_de_curseur()
    {
        var banc = new Banc { Appairee = false };
        var service = banc.Service();

        Assert.Equal(QuoiDeNeufService.Issue.Lu, await service.TourAsync(CancellationToken.None));
        Assert.Equal("/api/v1/agent/whats-new", banc.Site.Adresses[0]);
        Assert.Equal(0, banc.Releves);
        Assert.Null(banc.Suivi.Revision(SuiviDuSite.Travail, DateTime.UtcNow));
        Assert.True(banc.Suivi.RevisionAJour(SuiviDuSite.DefisOuverts, "c1", DateTime.UtcNow));
    }

    [Fact]
    public async Task Sans_identite_rien_ne_part()
    {
        var banc = new Banc { Credential = null };
        Assert.Equal(QuoiDeNeufService.Issue.SansIdentite, await banc.Service().TourAsync(CancellationToken.None));
        Assert.Empty(banc.Site.Adresses);
    }

    [Fact]
    public async Task Une_panne_recule_et_ne_couvre_rien()
    {
        var banc = new Banc();
        banc.Site.Impose = HttpStatusCode.ServiceUnavailable;
        var service = banc.Service();

        Assert.Equal(QuoiDeNeufService.Issue.Echec, await service.TourAsync(CancellationToken.None));
        Assert.False(banc.Suivi.QuoiDeNeufActif(DateTime.UtcNow));
        Assert.Equal(TimeSpan.FromMinutes(10), QuoiDeNeufService.Attente(QuoiDeNeufService.Issue.Echec, TimeSpan.FromMinutes(5), 1, 0.5));
        Assert.Equal(QuoiDeNeufService.PlafondDuRecul, QuoiDeNeufService.Attente(QuoiDeNeufService.Issue.Echec, TimeSpan.FromMinutes(5), 6, 0.5));
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(5, 60)]
    [InlineData(99999, 3600)]
    [InlineData(null, 300)]
    public async Task La_cadence_suit_poll_after_entre_une_minute_et_une_heure(int? demandee, int attendue)
    {
        var banc = new Banc();
        banc.Site.PollAfter = demandee;
        var service = banc.Service();

        await service.TourAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(attendue), service.Cadence);
    }

    [Fact]
    public async Task L_index_des_defis_ne_se_relit_que_si_sa_revision_change()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "qdn-defis-" + Guid.NewGuid().ToString("N"));
        try
        {
            var demandes = 0;
            var site = new SiteIndex(() => demandes++);
            var suivi = new SuiviDuSite();
            var index = new IndexDesDefisService(new FabriqueIndex(site), new CatalogueDesDefis(), dossier: dossier, suivi: suivi);

            // Sans quoi de neuf (site d'avant) : comme avant.
            await index.SynchroniserAsync(default);
            Assert.Equal(1, demandes);

            // La revision donnee est celle gardee : aucun appel, ni par la revision ni par le minuteur.
            await suivi.NoterLesRevisionsAsync(new Dictionary<string, string> { [SuiviDuSite.DefisOuverts] = "r1" }, TimeSpan.FromMinutes(10), default);
            await index.SynchroniserAsync(default);
            Assert.Equal(1, demandes);

            // Une autre revision : relue tout de suite.
            await suivi.NoterLesRevisionsAsync(new Dictionary<string, string> { [SuiviDuSite.DefisOuverts] = "r2" }, TimeSpan.FromMinutes(10), default);
            Assert.Equal(2, demandes);
        }
        finally
        {
            try { Directory.Delete(dossier, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class SiteIndex(Action demande) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            demande();
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"schema_version\":1,\"revision\":\"sha256:ab\",\"challenges\":[]}", Encoding.UTF8, "application/json"),
            };
            r.Headers.TryAddWithoutValidation("ETag", "\"r1\"");
            return Task.FromResult(r);
        }
    }

    private sealed class FabriqueIndex(HttpMessageHandler site) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(site, disposeHandler: false);
    }
}
