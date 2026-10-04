using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La file des brouillons scelles (piste B, 2026-10-04) : une partie mesuree n'est retiree qu'a un
/// verdict definitif, un brouillon modifie ne part jamais, et un renvoi rend le verdict d'origine.
/// </summary>
public sealed class BrouillonsDeScoreTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-brouillons-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _cle.Dispose();
        try { Directory.Delete(_dossier, recursive: true); } catch { }
    }

    private JsonObject Brouillon(string id, string fin, long pic = 45000)
    {
        var b = new JsonObject
        {
            ["schema"] = BrouillonDeScore.Schema,
            ["id"] = id,
            ["fin_le"] = fin,
            ["rom_group"] = "1942",
            ["pic"] = pic,
            ["run"] = new JsonArray(new JsonArray(10, 0), new JsonArray(900, pic)),
        };
        var spki = _cle.ExportSubjectPublicKeyInfo();
        BrouillonDeScore.Signer(b, Crypto.KeyId(spki), octets => Crypto.SignB64Url(_cle, octets));
        return b;
    }

    [Theory]
    [InlineData(200, "{\"ok\":true,\"status\":\"published\"}", IssueDEnvoi.Definitif)]
    [InlineData(200, "{\"ok\":true,\"status\":\"refused\"}", IssueDEnvoi.Definitif)]
    [InlineData(422, "{\"ok\":false,\"error\":\"format.schema\"}", IssueDEnvoi.Definitif)]
    [InlineData(503, "{\"ok\":false,\"error\":\"temporarily_unavailable\",\"retry_after\":30}", IssueDEnvoi.ARetenter)]
    [InlineData(500, "{\"ok\":false}", IssueDEnvoi.ARetenter)]
    [InlineData(429, "{\"ok\":false}", IssueDEnvoi.ARetenter)]
    [InlineData(401, "{\"ok\":false,\"error\":\"unauthorized\"}", IssueDEnvoi.ARetenter)]
    [InlineData(200, "<html>Plesk</html>", IssueDEnvoi.ARetenter)]
    [InlineData(502, "", IssueDEnvoi.ARetenter)]
    public void Une_reponse_se_classe_en_definitif_ou_a_retenter(int statut, string corps, IssueDEnvoi attendu)
    {
        Assert.Equal(attendu, BrouillonDeScore.Classer(statut, corps));
    }

    [Fact]
    public void Les_essais_s_espacent_de_30_s_a_30_min()
    {
        Assert.Equal(30, FileDesBrouillons.Delai(1, 0.5).TotalSeconds, 3);
        Assert.Equal(60, FileDesBrouillons.Delai(2, 0.5).TotalSeconds, 3);
        Assert.Equal(1800, FileDesBrouillons.Delai(20, 0.5).TotalSeconds, 3);
        Assert.InRange(FileDesBrouillons.Delai(1, 0).TotalSeconds, 23.9, 24.1);
        Assert.InRange(FileDesBrouillons.Delai(1, 1).TotalSeconds, 35.9, 36.1);
    }

    [Fact]
    public void Un_brouillon_modifie_a_la_main_est_refuse()
    {
        var spki = _cle.ExportSubjectPublicKeyInfo();
        var b = Brouillon("a", "2026-10-04T08:00:00.000Z");
        Assert.True(BrouillonDeScore.Verifier(b, spki));

        b["pic"] = 999999;
        Assert.False(BrouillonDeScore.Verifier(b, spki));

        using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(BrouillonDeScore.Verifier(Brouillon("b", "2026-10-04T08:00:00.000Z"), autre.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void La_file_rend_le_plus_ancien_d_abord_et_ne_retire_qu_au_verdict()
    {
        var maintenant = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var file = new FileDesBrouillons(_dossier, () => maintenant, new Random(1));
        Assert.True(file.Poser(Brouillon("recent", "2026-10-04T08:30:00.000Z")));
        Assert.True(file.Poser(Brouillon("ancien", "2026-10-04T08:00:00.000Z")));
        File.WriteAllText(Path.Combine(_dossier, "casse.json"), "{pas du json");

        var attente = file.EnAttente();
        Assert.Equal(new[] { "ancien", "recent" }, attente.Select(b => b.Id));
        Assert.True(File.Exists(Path.Combine(_dossier, "illisibles", "casse.json")));

        // Echec : le brouillon reste et attend son tour.
        var delai = file.Reporter("ancien");
        Assert.False(file.EstDu("ancien"));
        Assert.True(file.EstDu("recent"));
        maintenant += delai;
        Assert.True(file.EstDu("ancien"));
        Assert.Equal(1, file.Essais("ancien"));

        // Verdict : il quitte la file.
        file.Retirer("ancien");
        Assert.Equal(new[] { "recent" }, file.EnAttente().Select(b => b.Id));
    }

    [Fact]
    public void Un_doublon_rend_son_verdict_d_origine()
    {
        var corps = "{\"ok\":true,\"status\":\"duplicate\",\"reason\":\"session.duplicate\",\"original_status\":\"published\",\"original_reason\":\"\",\"rank\":12}";
        var verdict = JsonNode.Parse(NelfePlayScoringReporter.VerdictDOrigine(corps))!.AsObject();
        Assert.Equal("published", (string?)verdict["status"]);
        Assert.Equal(12, (int?)verdict["rank"]);
        Assert.True((bool?)verdict["duplicate"]);

        var publie = "{\"ok\":true,\"status\":\"published\",\"rank\":3}";
        Assert.Equal(publie, NelfePlayScoringReporter.VerdictDOrigine(publie));
    }
}
