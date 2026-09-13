using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La lecture du classement et le decoupage en vues. Le vecteur est une REPONSE REELLE de la
/// plateforme, prise sur cette borne : un contrat qui derive doit se voir ici, pas a l'ecran.
/// </summary>
public class LeaderboardClientTests
{
    // Reponse reelle de https://nelfeplay.com/api/v1/scores/board?game=19xx-...&limit=3
    private const string Reel = """
        {"ok":true,"world":"","rows":[{"game":"19xx-the-war-against-destiny","game_name":"19xx the War Against Destiny",
        "system":"arcade","ruleset":"1cc","world":"home","player":"Nelfe80","anonymous":false,"avatar":"a:78E53768A3D4",
        "venue":"","city":"Paris","country":"FR","channel":"","contest_id":"","value":38600,"at":"2026-09-06 07:46:47",
        "avatar_identity":{"pseudo":"Nelfe80","family":"platformer","variation":872,"palette":[]},
        "handle":"cd0edd1cac6911f1","certificate":"/records/arcade/19xx.../","sealed":true,"replay":null}]}
        """;

    [Fact]
    public void Une_reponse_reelle_se_lit_entierement()
    {
        var lignes = LeaderboardClient.Lire(Reel, "Nelfe80");
        var l = Assert.Single(lignes);
        Assert.Equal(1, l.Rang);
        Assert.Equal("Nelfe80", l.Joueur);
        Assert.Equal(38600, l.Valeur);
        Assert.Equal("Paris", l.Ville);
        Assert.Equal("FR", l.Pays);
        Assert.True(l.Scelle);
        Assert.Null(l.ReplayId);
        Assert.True(l.CestMoi);
    }

    [Fact]
    public void Ma_ligne_ne_se_reconnait_qu_a_mon_pseudo()
    {
        Assert.False(Assert.Single(LeaderboardClient.Lire(Reel, "QuelquUnDAutre")).CestMoi);
        Assert.False(Assert.Single(LeaderboardClient.Lire(Reel, "")).CestMoi);
    }

    [Fact]
    public void Un_score_anonyme_ne_porte_pas_de_nom()
    {
        var json = """{"ok":true,"rows":[{"player":"Quelqu'un","anonymous":true,"value":10}]}""";
        var l = Assert.Single(LeaderboardClient.Lire(json, "Quelqu'un"));
        Assert.Equal("", l.Joueur);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"rows":"pas une liste"}""")]
    public void Un_corps_illisible_rend_un_classement_vide_sans_jeter(string json)
        => Assert.Empty(LeaderboardClient.Lire(json, "Nelfe80"));

    // ── Le decoupage en vues ─────────────────────────────────────────────────

    private static readonly IReadOnlyList<LeaderboardClient.Ligne> Monde = new[]
    {
        new LeaderboardClient.Ligne(1, "Ayumi", 9000, "Tokyo", "JP", "", true, "rp_1", "", false),
        new LeaderboardClient.Ligne(2, "Nelfe80", 8000, "Paris", "FR", "Le Rétro", true, null, "", true),
        new LeaderboardClient.Ligne(3, "Marc", 7000, "Paris", "FR", "", false, "rp_3", "", false),
        new LeaderboardClient.Ligne(4, "Luis", 6000, "Madrid", "ES", "Le Rétro", false, null, "", false),
    };

    [Fact]
    public void Le_monde_est_rendu_tel_quel()
        => Assert.Equal(4, LeaderboardClient.Tailler(Monde, LeaderboardPanelModel.Vue.Monde, "Paris", "FR", "Le Rétro").Count);

    [Fact]
    public void Ma_ville_et_mon_pays_filtrent_sans_renumeroter()
    {
        var ville = LeaderboardClient.Tailler(Monde, LeaderboardPanelModel.Vue.MaVille, "Paris", "FR", "");
        Assert.Equal(new[] { "Nelfe80", "Marc" }, ville.Select(l => l.Joueur).ToArray());
        // Les rangs restent MONDIAUX : etre 2e mondial dit quelque chose, « 1er de ma ville »
        // recalcule ne dirait que « premier de la liste que je viens de filtrer ».
        Assert.Equal(new[] { 2, 3 }, ville.Select(l => l.Rang).ToArray());

        var pays = LeaderboardClient.Tailler(Monde, LeaderboardPanelModel.Vue.MonPays, "Paris", "FR", "");
        Assert.Equal(new[] { 2, 3 }, pays.Select(l => l.Rang).ToArray());
    }

    [Fact]
    public void Ma_salle_ne_prend_que_la_salle_nommee()
    {
        var salle = LeaderboardClient.Tailler(Monde, LeaderboardPanelModel.Vue.MaSalle, "Paris", "FR", "Le Rétro");
        Assert.Equal(new[] { "Nelfe80", "Luis" }, salle.Select(l => l.Joueur).ToArray());
    }

    [Fact]
    public void Sans_ville_ni_salle_connue_la_vue_est_vide_et_non_pleine()
    {
        // Le piege serait de tout montrer quand on ne sait pas filtrer : le joueur croirait que
        // toute la planete habite chez lui.
        Assert.Empty(LeaderboardClient.Tailler(Monde, LeaderboardPanelModel.Vue.MaVille, "", "FR", ""));
        Assert.Empty(LeaderboardClient.Tailler(Monde, LeaderboardPanelModel.Vue.MaSalle, "Paris", "FR", ""));
    }

    [Fact]
    public void Cette_borne_et_mes_records_ne_montrent_que_moi()
    {
        foreach (var vue in new[] { LeaderboardPanelModel.Vue.CetteBorne, LeaderboardPanelModel.Vue.MesRecords })
        {
            var mien = LeaderboardClient.Tailler(Monde, vue, "Paris", "FR", "Le Rétro");
            Assert.Equal("Nelfe80", Assert.Single(mien).Joueur);
        }
    }
}
