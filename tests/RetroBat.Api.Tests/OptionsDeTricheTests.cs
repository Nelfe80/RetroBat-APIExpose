using RetroBat.Api.Leaderboard;
using RetroBat.Api.Scoring;
using Xunit;
using Partie = RetroBat.Api.Leaderboard.LocalPlaysIndex.Partie;
using ReplayLocal = RetroBat.Api.Leaderboard.LocalPlaysIndex.ReplayLocal;

namespace RetroBat.Api.Tests;

/// <summary>
/// 24/11/2026 : une option de triche du coeur activee rend la partie non certifiable (Batsugun Special de NeoGeoff, 14/11),
/// et MES RECORDS grise ce que le site n'a pas classe.
/// </summary>
public sealed class OptionsDeTricheTests
{
    [Fact]
    public void L_option_de_Batsugun_est_une_triche_active_et_nommee()
    {
        var noms = OptionsDeTriche.Actives(
            "fbneo-cheat-3-batsugunsp-Always_have_Max_Weapon_PL1=1 - Always have Max Weapon PL1;fbneo-force-60hz=enabled");
        Assert.Equal(new[] { "Always have Max Weapon PL1" }, noms);
    }

    [Theory]
    [InlineData("fbneo-cheat-3-batsugunsp-Always_have_Max_Weapon_PL1=0 - Disabled")]
    [InlineData("fbneo-cheat-1-x-Infinite_Lives=disabled")]
    [InlineData("fbneo-cheat-1-x-Infinite_Lives=")]
    [InlineData("mame_cheats_enable=disabled")]
    [InlineData("mame_cheats_enable=off")]
    [InlineData("fbneo-cheat-1-x-Y=0")]
    [InlineData("fbneo-dipswitch-1942-Difficulty=Hard;fbneo-neogeo-mode=DIPSWITCH")]
    [InlineData("fbneo-dipswitch-batsugunsp-Cheating=Normal game")]
    [InlineData("fbneo-dipswitch-batsugun-Cheating=\"Normal game\"")]
    [InlineData("fbneo-dipswitch-mspacman-Rack_Test_(Cheat)=Off")]
    [InlineData("")]
    public void Une_option_desactivee_ou_vide_ne_declenche_rien(string options)
        => Assert.Empty(OptionsDeTriche.Actives(options));

    [Theory]
    [InlineData("mame_cheats_enable=enabled")]
    [InlineData("fbneo-cheat-1-x-Infinite_Lives=enabled")]
    [InlineData("fbneo-cheat-1-x-Infinite_Lives=1 - Infinite")]
    [InlineData("fbneo-cheat-1-x-Level=05 - Stage 5")]
    [InlineData("snes9x_cheat_bidule=on")]
    [InlineData("fbneo-dipswitch-batsugunsp-Cheating=No death & stop")]
    [InlineData("fbneo-dipswitch-mspacman-Rack_Test_(Cheat)=On")]
    public void Une_option_activee_declenche(string options)
        => Assert.Single(OptionsDeTriche.Actives(options));

    [Fact]
    public void Un_jeu_qui_liste_toutes_ses_triches_desactivees_reste_certifiable()
        => Assert.Empty(OptionsDeTriche.Actives(
            "fbneo-cheat-1-bs-A=0 - Disabled;fbneo-cheat-2-bs-B=0 - Disabled;fbneo-cheat-3-bs-C=0 - Disabled"));

    [Fact]
    public void Le_motif_est_celui_du_passeport()
        => Assert.Equal("game.cheat_enabled", OptionsDeTriche.Motif);

    private static readonly DateTime T0 = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    private static Partie P(long score, int minutes, string verdict, string motif = "")
        => new("s" + score, "batsugun", "1cc", score, false, T0.AddMinutes(minutes), T0.AddMinutes(minutes + 5), verdict, "home", "", false, motif);

    [Fact]
    public void Mes_records_grise_ce_qui_n_est_pas_publie_et_donne_le_motif()
    {
        var parties = new[]
        {
            P(500_000, 0, "published"),
            P(822_180, 10, "quarantined", "settings.unknown"),
            P(100_000, 20, "refused", "game.cheat_enabled"),
        };
        var lignes = LocalPlaysIndex.MesParties(parties, "batsugun", "1cc", "", Array.Empty<ReplayLocal>(),
            d => d.ToString("dd/MM"), raison => "motif:" + raison);
        Assert.Equal(new long[] { 822_180, 500_000 }, lignes.Select(l => l.Valeur));   // le refus n'y figure pas
        Assert.True(lignes[0].Grise);
        Assert.Equal("settings.unknown", lignes[0].Motif);
        Assert.EndsWith("motif:settings.unknown", lignes[0].Joueur);
        Assert.False(lignes[1].Grise);
        Assert.DoesNotContain("motif", lignes[1].Joueur);
    }

    [Fact]
    public void Le_motif_se_lit_dans_la_reponse_du_site()
    {
        const string json = """
            {"session_id":"abc","verdict":"quarantined","submitted_at":"2026-11-14T20:10:00Z",
             "server_response":"{\"status\":\"quarantined\",\"reason\":\"settings.unknown\"}",
             "passport":{"game":{"rom_group":"batsugun","ruleset":"1cc"},"context":{"world":"home"},"identity":{},
               "timing":{"started_at":"2026-11-14T20:00:00Z","ended_at":"2026-11-14T20:05:00Z"},
               "metric":{"value":"822180","ranking_direction":"higher_better"}}}
            """;
        var p = LocalPlaysIndex.Lire(json)!;
        Assert.Equal("settings.unknown", p.Motif);
        Assert.True(LocalPlaysIndex.Grisee(p));
    }
}
