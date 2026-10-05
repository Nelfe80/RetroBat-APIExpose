using System.Text;
using System.Text.Json.Nodes;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La synchro post-apocalypse (2026-10-05) : apres une restauration du site, la borne renvoie les
/// parties de la fenetre perdue, ne les marque envoyees que sur un vrai verdict, et sait se faire
/// reconnaitre d'une base qui l'a oubliee.
/// </summary>
public sealed class RecuperationDesPartiesTests
{
    private static JsonObject Statut(string? since, string? armedAt) => new()
    {
        ["ok"] = true, ["contribute"] = true, ["epoch"] = "e", ["since"] = since, ["armed_at"] = armedAt,
    };

    [Fact]
    public void La_fenetre_va_de_la_sauvegarde_a_l_armement_plus_une_heure()
    {
        var f = RecuperationDesParties.LireLaFenetre(Statut("2026-10-05T17:45:00Z", "2026-10-05T20:00:00Z"));
        Assert.Equal(new DateTime(2026, 10, 5, 17, 45, 0, DateTimeKind.Utc), f.Depuis);
        Assert.Equal(new DateTime(2026, 10, 5, 21, 0, 0, DateTimeKind.Utc), f.Jusqua);
        Assert.True(f.Contient(new DateTime(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc)));
        Assert.False(f.Contient(new DateTime(2026, 10, 5, 17, 0, 0, DateTimeKind.Utc)));
        Assert.False(f.Contient(new DateTime(2026, 10, 5, 22, 0, 0, DateTimeKind.Utc)));
        // Une partie sans heure ne se range pas dans une fenetre bornee.
        Assert.False(f.Contient(null));
    }

    [Fact]
    public void Un_ancien_armement_sans_heure_de_sauvegarde_renvoie_tout()
    {
        var f = RecuperationDesParties.LireLaFenetre(new JsonObject { ["contribute"] = true, ["epoch"] = "e" });
        Assert.Same(RecuperationDesParties.Fenetre.Tout, f);
        Assert.True(f.Contient(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(f.Contient(null));
    }

    [Fact]
    public void L_heure_d_un_record_est_celle_du_verdict_sinon_la_fin_de_partie()
    {
        Assert.Equal(new DateTime(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc),
            RecuperationDesParties.HeureDuRecord(new JsonObject { ["submitted_at"] = "2026-10-05T19:00:00Z" }));
        Assert.Equal(new DateTime(2026, 10, 5, 18, 30, 0, DateTimeKind.Utc),
            RecuperationDesParties.HeureDuRecord(new JsonObject
            {
                ["passport"] = new JsonObject { ["timing"] = new JsonObject { ["ended_at"] = "2026-10-05T18:30:00Z" } },
            }));
        Assert.Null(RecuperationDesParties.HeureDuRecord(new JsonObject()));
    }

    [Theory]
    [InlineData(200, "{\"ok\":true,\"status\":\"published\",\"reason\":\"\"}", RecuperationDesParties.Issue.Fait)]
    [InlineData(200, "{\"ok\":true,\"status\":\"duplicate\",\"reason\":\"session.duplicate\"}", RecuperationDesParties.Issue.Fait)]
    [InlineData(200, "{\"ok\":true,\"status\":\"held\",\"reason\":\"plausibility.macro_detected\"}", RecuperationDesParties.Issue.Fait)]
    [InlineData(200, "{\"ok\":false,\"status\":\"refused\",\"reason\":\"profile.not_open\"}", RecuperationDesParties.Issue.Fait)]
    // Jusqu'ici marque envoye : la cle avait ete inscrite apres la sauvegarde.
    [InlineData(200, "{\"ok\":false,\"status\":\"refused\",\"reason\":\"session.device_unknown\"}", RecuperationDesParties.Issue.CleInconnue)]
    [InlineData(200, "{\"ok\":false,\"status\":\"refused\",\"reason\":\"session.device_mismatch\"}", RecuperationDesParties.Issue.AutreIdentite)]
    [InlineData(200, "{\"ok\":false,\"status\":\"refused\",\"reason\":\"server.unavailable\"}", RecuperationDesParties.Issue.ARetenter)]
    [InlineData(401, "{\"error\":\"unauthorized\"}", RecuperationDesParties.Issue.BorneInconnue)]
    [InlineData(409, "{}", RecuperationDesParties.Issue.Fait)]
    [InlineData(400, "{\"error\":\"invalid_passport\"}", RecuperationDesParties.Issue.Fait)]
    [InlineData(503, "{\"error\":\"unavailable\"}", RecuperationDesParties.Issue.ARetenter)]
    [InlineData(429, "", RecuperationDesParties.Issue.ARetenter)]
    [InlineData(200, "<html>maintenance</html>", RecuperationDesParties.Issue.ARetenter)]
    public void Un_renvoi_ne_vaut_que_sur_un_vrai_verdict(int http, string corps, RecuperationDesParties.Issue attendu)
        => Assert.Equal(attendu, RecuperationDesParties.Classer(http, corps));

    private static JsonObject Record(string device, string? heure, bool ticketSigne = true, string? ticketDevice = null) => new()
    {
        ["submitted_at"] = heure,
        ["passport"] = new JsonObject
        {
            ["session_id"] = "s_" + heure,
            ["device"] = new JsonObject { ["device_id"] = device },
            ["ticket"] = new JsonObject
            {
                ["device_id"] = ticketDevice ?? device,
                ["signature"] = ticketSigne ? "sig" : null,
            },
        },
    };

    [Fact]
    public void La_preuve_s_appuie_sur_le_passeport_le_plus_recent_de_l_appareil()
    {
        var records = new[]
        {
            Record("aaa", "2026-10-05T18:00:00Z"),
            Record("aaa", "2026-10-05T19:30:00Z"),
            Record("bbb", "2026-10-05T19:50:00Z"),
            Record("aaa", "2026-10-05T19:55:00Z", ticketSigne: false),
            Record("aaa", "2026-10-05T19:58:00Z", ticketDevice: "ccc"),
        };
        var p = RecuperationDesParties.PasseportDeLAppareil(records, "aaa");
        Assert.Equal("s_2026-10-05T19:30:00Z", (string?)p?["session_id"]);
        Assert.Null(RecuperationDesParties.PasseportDeLAppareil(records, "zzz"));
    }

    [Fact]
    public void Le_message_de_preuve_est_celui_que_le_site_verifie()
    {
        // RecoveryEpisode::messageDePreuve (site) : JCS, cles triees.
        var attendu = "{\"credential_sha256\":\"c0ffee\",\"device_id\":\"0123abcd\",\"epoch\":\"2026-10-05T20:00:00Z-0badcafe\","
            + "\"purpose\":\"nelfeplay.recovery.reidentify\"}";
        Assert.Equal(attendu, Encoding.UTF8.GetString(
            RecuperationDesParties.MessageDePreuve("0123abcd", "c0ffee", "2026-10-05T20:00:00Z-0badcafe")));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", RecuperationDesParties.Sha256Hex(""));
    }

    [Theory]
    [InlineData("{\"ok\":false,\"status\":\"refused\",\"reason\":\"session.device_unknown\"}", IssueDEnvoi.ARetenter)]
    [InlineData("{\"ok\":false,\"status\":\"refused\",\"reason\":\"submission.write_failed\"}", IssueDEnvoi.ARetenter)]
    [InlineData("{\"ok\":false,\"status\":\"refused\",\"reason\":\"profile.not_open\"}", IssueDEnvoi.Definitif)]
    [InlineData("{\"ok\":true,\"status\":\"published\",\"reason\":\"\"}", IssueDEnvoi.Definitif)]
    public void Un_brouillon_refuse_pour_cle_inconnue_reste_en_file(string corps, IssueDEnvoi attendu)
        => Assert.Equal(attendu, BrouillonDeScore.Classer(200, corps));
}
