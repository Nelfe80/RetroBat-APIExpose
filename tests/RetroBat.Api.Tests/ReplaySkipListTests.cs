using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Controllers;
using RetroBat.Api.Replay.Recording;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La liste « ne s'enregistre plus » expire (16/11, decision user) : la route locale que l'outil de diagnostic lit et leve,
/// et le bandeau qui donne au joueur la date du prochain essai.
/// </summary>
public sealed class ReplaySkipListTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-skiplist-" + Guid.NewGuid().ToString("N"));
    private DateTime _maintenant = new(2026, 11, 15, 21, 4, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { }
    }

    private GardeDesPlantages Garde() => new(Path.Combine(_dossier, "sans-enregistrement.json"), () => _maintenant);

    private static string Cle(string jeu) => GardeDesPlantages.Cle("fbneo", jeu, "a0b1c2d3", "c0ffee0123456789", "1.22.2+123+20250101000000");

    private void Planter(GardeDesPlantages garde, string jeu)
    {
        garde.JeuLance();
        garde.EcouteVue("c0ffee0123456789");
        garde.EnregistrementLance(Cle(jeu), jeu, "1.22.2+123+20250101000000");
        garde.JeuTermine();
        _maintenant = _maintenant.AddSeconds(6);
        Assert.NotNull(garde.Juger());
    }

    private static ReplaySkipListController Route(GardeDesPlantages garde, IPAddress? ip = null, string? corps = null)
    {
        var contexte = new DefaultHttpContext();
        contexte.Connection.RemoteIpAddress = ip ?? IPAddress.Loopback;
        contexte.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(corps ?? ""));
        return new ReplaySkipListController(garde) { ControllerContext = new ControllerContext { HttpContext = contexte } };
    }

    private static JsonElement Json(IActionResult r)
        => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value);

    [Fact]
    public void La_liste_donne_le_jeu_la_date_le_motif_et_le_prochain_essai()
    {
        var garde = Garde();
        Planter(garde, "19xx");
        var r = Json(Route(garde).List());

        Assert.Equal(1, r.GetProperty("active").GetInt32());
        Assert.Equal(7, r.GetProperty("base_days").GetDouble());
        var jeu = Assert.Single(r.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(Cle("19xx"), jeu.GetProperty("key").GetString());
        Assert.Equal("fbneo", jeu.GetProperty("system").GetString());
        Assert.Equal("19xx", jeu.GetProperty("game").GetString());
        Assert.Equal(VoyantDuReplay.MotifPlantage, jeu.GetProperty("reason").GetString());
        Assert.Equal(1, jeu.GetProperty("crash_count").GetInt32());
        Assert.True(jeu.GetProperty("active").GetBoolean());
        Assert.Equal(_maintenant.AddDays(7), jeu.GetProperty("next_try").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Remettre_a_zero_leve_toute_la_liste_et_garde_l_historique()
    {
        var garde = Garde();
        Planter(garde, "19xx");
        Planter(garde, "gunlock");

        var r = Json(await Route(garde).Reset());
        Assert.Equal(2, r.GetProperty("lifted").GetInt32());
        Assert.Equal(0, r.GetProperty("remaining").GetInt32());
        Assert.Null(garde.RenoncementPour(Cle("19xx")));
        Assert.Null(garde.RenoncementPour(Cle("gunlock")));

        // L'historique reste : la liste les montre, inactifs, avec leur levee.
        var liste = Json(Route(garde).List());
        Assert.Equal(0, liste.GetProperty("active").GetInt32());
        Assert.All(liste.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.String, i.GetProperty("lifted_at").ValueKind));
    }

    [Fact]
    public async Task Retirer_un_jeu_ne_leve_que_lui()
    {
        var garde = Garde();
        Planter(garde, "19xx");
        Planter(garde, "gunlock");

        var r = Json(await Route(garde, corps: JsonSerializer.Serialize(new { key = Cle("gunlock") })).Reset());
        Assert.Equal(1, r.GetProperty("lifted").GetInt32());
        Assert.Equal(1, r.GetProperty("remaining").GetInt32());
        Assert.NotNull(garde.RenoncementPour(Cle("19xx")));
        Assert.Null(garde.RenoncementPour(Cle("gunlock")));

        // Par le parametre aussi.
        Json(await Route(garde).Reset(key: Cle("19xx")));
        Assert.Null(garde.RenoncementPour(Cle("19xx")));
    }

    [Fact]
    public async Task Une_cle_inconnue_ou_un_corps_illisible_ne_levent_rien()
    {
        var garde = Garde();
        Planter(garde, "19xx");

        Assert.IsType<NotFoundObjectResult>(await Route(garde).Reset(key: "fbneo/autre"));
        Assert.IsType<BadRequestObjectResult>(await Route(garde, corps: "{cle:").Reset());
        Assert.NotNull(garde.RenoncementPour(Cle("19xx")));
    }

    [Fact]
    public async Task Depuis_le_reseau_la_liste_n_existe_pas()
    {
        var garde = Garde();
        Planter(garde, "19xx");
        var reseau = IPAddress.Parse("192.168.1.20");

        Assert.IsType<NotFoundResult>(Route(garde, reseau).List());
        Assert.IsType<NotFoundResult>(await Route(garde, reseau).Reset());
        Assert.NotNull(garde.RenoncementPour(Cle("19xx")));
    }

    // ── le bandeau du joueur ────────────────────────────────────────────────

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    public void Le_bandeau_donne_la_date_du_prochain_essai(string langue)
    {
        var le = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var essai = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var texte = CabinetAnnounceText.Get("replay_off_crash_retry_sub", langue);
        Assert.NotEqual("replay_off_crash_retry_sub", texte);
        Assert.Contains("{0}", texte);
        Assert.Contains("{1}", texte);
        if (langue != "en") Assert.NotEqual(CabinetAnnounceText.Get("replay_off_crash_retry_sub", "en"), texte);

        var a = VoyantDuReplay.SansReplay(langue, VoyantDuReplay.MotifPlantage, le, essai);
        Assert.Contains(le.ToLocalTime().ToString("yyyy-MM-dd"), a.Detail);
        Assert.Contains(essai.ToLocalTime().ToString("yyyy-MM-dd"), a.Detail);
    }

    [Fact]
    public void Sans_date_d_essai_le_bandeau_garde_l_ancien_texte()
    {
        var le = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var a = VoyantDuReplay.SansReplay("fr", VoyantDuReplay.MotifPlantage, le);
        Assert.Equal(string.Format(CabinetAnnounceText.Get("replay_off_crash_sub", "fr"), le.ToLocalTime().ToString("yyyy-MM-dd")), a.Detail);
    }
}
