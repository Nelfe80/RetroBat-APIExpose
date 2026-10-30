using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'INDEX DES DEFIS SUR LA BORNE (lot P3) : le contrat de l'index, le reglement qui fait foi, et le service qui garde la
/// derniere copie valide. Le reglement est celui de Green Hill 1 tel que depose sur le site.
/// </summary>
public class SpeedrunIndexTests
{
    private static readonly byte[] Document = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json"));

    private const string Url = "/api/v1/challenges/sonic-the-hedgehog.green-hill-1/1";

    private static JsonObject Entree(byte[]? document = null) => new()
    {
        ["challenge_id"] = "sonic-the-hedgehog.green-hill-1", ["version"] = 1, ["ruleset"] = "ch-sonic-green-hill-1-v1",
        ["schema"] = "nelfe.challenge.v2", ["kind"] = "speedrun_level", ["start_policy"] = "normal_boot",
        ["system_id"] = "megadrive", ["rom_group"] = "sonic-the-hedgehog",
        ["mem_sha256"] = "00bf82c7b8783299fde03272c77541049f18e8cc4dd0a54ae5d6fe7e5dcdebe6",
        ["cores"] = new JsonArray("genesis_plus_gx"),
        ["document_sha256"] = Crypto.Sha256Hex(document ?? Document), ["document_url"] = Url,
    };

    private static string Index(params JsonObject[] defis) => new JsonObject
    {
        ["ok"] = true, ["schema_version"] = 1, ["revision"] = "sha256:ab",
        ["challenges"] = new JsonArray(defis.Select(d => (JsonNode)d).ToArray()),
    }.ToJsonString();

    private static DefiDeLIndex Defi(JsonObject entree) => IndexDesDefis.Lire(Index(entree)).Lecture!.Defis.Single();

    [Theory]
    [InlineData("pas du json", "reponse illisible")]
    [InlineData("""{"ok":false}""", "reponse refusee")]
    [InlineData("""{"ok":true,"schema_version":2,"revision":"x","challenges":[]}""", "schema inconnu")]
    [InlineData("""{"ok":true,"schema_version":1,"challenges":[]}""", "revision absente")]
    [InlineData("""{"ok":true,"schema_version":1,"revision":"x"}""", "liste de defis absente")]
    public void Une_reponse_hors_contrat_n_est_jamais_une_liste_vide(string json, string attendu)
    {
        var (lecture, erreur) = IndexDesDefis.Lire(json);
        Assert.Null(lecture);
        Assert.Equal(attendu, erreur);
    }

    [Fact]
    public void Un_schema_inconnu_ou_un_document_hors_du_site_s_ecarte_seul()
    {
        var futur = Entree();
        futur["schema"] = "nelfe.challenge.v9";
        var ailleurs = Entree();
        ailleurs["document_url"] = "https://exemple.invalid/defi";
        var remonte = Entree();
        remonte["document_url"] = "/api/v1/challenges/../secret";

        var lecture = IndexDesDefis.Lire(Index(futur, ailleurs, remonte, Entree())).Lecture!;

        Assert.Single(lecture.Defis);
        Assert.Equal(3, lecture.Ecartes.Count);
    }

    [Fact]
    public void Le_reglement_de_green_hill_1_passe_le_controle()
    {
        var (reglement, erreur) = IndexDesDefis.Controler(Defi(Entree()), Document);
        Assert.Null(erreur);
        Assert.Equal("ch-sonic-green-hill-1-v1", reglement?.Ruleset);
    }

    [Fact]
    public void Des_octets_modifies_sont_refuses_par_leur_empreinte()
    {
        var modifie = Document.ToArray();
        modifie[10] ^= 1;
        Assert.Equal("empreinte du document fausse", IndexDesDefis.Controler(Defi(Entree()), modifie).Erreur);
    }

    [Theory]
    [InlineData("ruleset", "ch-autre-v1")]
    [InlineData("rom_group", "sonic-2")]
    [InlineData("mem_sha256", "ffff")]
    public void L_index_ne_contredit_pas_le_reglement(string champ, string valeur)
    {
        var entree = Entree();
        entree[champ] = valeur;
        var erreur = IndexDesDefis.Controler(Defi(entree), Document).Erreur;
        Assert.Contains(champ, erreur);
    }

    private sealed class Site(Func<HttpRequestMessage, HttpResponseMessage> repondre) : HttpMessageHandler
    {
        public List<string> Demandes { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
        {
            Demandes.Add(requete.RequestUri!.AbsolutePath);
            return Task.FromResult(repondre(requete));
        }
    }

    private sealed class Fabrique(HttpMessageHandler site) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(site, disposeHandler: false);
    }

    private static HttpResponseMessage Texte(string corps, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corps, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.TryAddWithoutValidation("ETag", etag);
        return r;
    }

    private static HttpResponseMessage Octets(byte[] octets) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(octets) };

    private static string Dossier() => Path.Combine(Path.GetTempPath(), "defis-" + Guid.NewGuid().ToString("N"));

    private static Func<HttpRequestMessage, HttpResponseMessage> SiteOuvert(string? index = null) => r
        => r.RequestUri!.AbsolutePath == Url ? Octets(Document) : Texte(index ?? Index(Entree()), "\"r1\"");

    [Fact]
    public async Task Le_service_remplit_le_catalogue_et_garde_la_copie_pour_le_prochain_demarrage()
    {
        var dossier = Dossier();
        try
        {
            var catalogue = new CatalogueDesDefis();
            await new IndexDesDefisService(new Fabrique(new Site(SiteOuvert())), catalogue, dossier: dossier).SynchroniserAsync(default);
            Assert.NotNull(catalogue.Trouver("sonic-the-hedgehog.green-hill-1"));

            // Sans reseau, une API qui redemarre repart de la copie locale.
            var suivant = new CatalogueDesDefis();
            new IndexDesDefisService(new Fabrique(new Site(_ => throw new HttpRequestException())), suivant, dossier: dossier).ChargerDuDisque();
            Assert.Equal("ch-sonic-green-hill-1-v1", suivant.Trouver("sonic-the-hedgehog.green-hill-1")?.Ruleset);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public async Task Un_defi_retire_du_site_quitte_le_catalogue_et_son_fichier_part()
    {
        var dossier = Dossier();
        try
        {
            var catalogue = new CatalogueDesDefis();
            await new IndexDesDefisService(new Fabrique(new Site(SiteOuvert())), catalogue, dossier: dossier).SynchroniserAsync(default);
            await new IndexDesDefisService(new Fabrique(new Site(_ => Texte(Index()))), catalogue, dossier: dossier).SynchroniserAsync(default);

            Assert.Empty(catalogue.Tous);
            Assert.False(File.Exists(Path.Combine(dossier, Crypto.Sha256Hex(Document) + ".json")));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public async Task Le_document_n_est_repris_qu_une_fois_et_un_304_ne_change_rien()
    {
        var dossier = Dossier();
        try
        {
            var catalogue = new CatalogueDesDefis();
            var site = new Site(r => r.Headers.TryGetValues("If-None-Match", out _)
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : SiteOuvert()(r));
            var service = new IndexDesDefisService(new Fabrique(site), catalogue, dossier: dossier);

            await service.SynchroniserAsync(default);
            await service.SynchroniserAsync(default);

            Assert.Single(site.Demandes, d => d == Url);
            Assert.Single(catalogue.Tous);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Un_site_en_panne_ne_vide_pas_le_catalogue(HttpStatusCode code)
    {
        var dossier = Dossier();
        try
        {
            var catalogue = new CatalogueDesDefis();
            await new IndexDesDefisService(new Fabrique(new Site(SiteOuvert())), catalogue, dossier: dossier).SynchroniserAsync(default);
            await new IndexDesDefisService(new Fabrique(new Site(_ => new HttpResponseMessage(code))), catalogue, dossier: dossier).SynchroniserAsync(default);
            Assert.Single(catalogue.Tous);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public async Task Un_reglement_injoignable_laisse_le_catalogue_en_place_et_se_reprend_au_passage_suivant()
    {
        var dossier = Dossier();
        try
        {
            var catalogue = new CatalogueDesDefis();
            await new IndexDesDefisService(new Fabrique(new Site(r => r.RequestUri!.AbsolutePath == Url
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : Texte(Index(Entree()), "\"r1\""))), catalogue, dossier: dossier).SynchroniserAsync(default);
            Assert.Empty(catalogue.Tous);
            Assert.False(File.Exists(Path.Combine(dossier, "index.json")));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }
}
