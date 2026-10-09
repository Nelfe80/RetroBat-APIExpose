using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE 1LC, SCORE DE LA PREMIERE VIE (decision user du 2026-10-09). La meme partie donne son 1CC et son
/// 1LC, chacun a son classement. Les mesures viennent du labo d'Alex Kidd in Miracle World : trois vies,
/// la premiere perdue a la frame 1318 (vies 3 puis 2), score garde d'une vie a l'autre.
/// </summary>
public class PremiereVieTests
{
    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? Regle(JsonElement? p) => p?.GetProperty("ruleset").GetString();

    private static readonly List<(long frame, long total)> Run =
    [
        (760, 0), (900, 200), (1200, 600), (1500, 800), (2300, 2800),
    ];

    [Fact]
    public void Le_1LC_s_arrete_a_la_premiere_vie_perdue()
    {
        var (run, mort) = PremiereVie.Couper(Run, [(1318L, 1), (2089L, 1)]);
        Assert.Equal(1318, mort);
        Assert.Equal(600, run[^1].total);
        Assert.Equal(3, run.Count);
    }

    [Fact]
    public void Sans_vie_perdue_le_1LC_est_le_run_entier()
    {
        var (run, mort) = PremiereVie.Couper(Run, []);
        Assert.Null(mort);
        Assert.Equal(2800, run[^1].total);
    }

    [Fact]
    public void Une_mort_d_une_partie_precedente_ne_compte_pas()
    {
        // Le run retenu commence a la frame 760 : la mort de la frame 400 est celle d'une autre partie.
        var (_, mort) = PremiereVie.Couper(Run, [(400L, 1), (1318L, 1)]);
        Assert.Equal(1318, mort);
    }

    [Fact]
    public void La_mort_du_joueur_2_ne_coupe_pas_le_1LC_du_joueur_1()
    {
        var (_, mort) = PremiereVie.Couper(Run, [(1000L, 2), (2089L, 1)]);
        Assert.Equal(2089, mort);
    }

    [Fact]
    public void Une_lecture_a_la_frame_de_la_mort_reste_dans_le_1LC()
    {
        var (run, _) = PremiereVie.Couper(Run, [(1500L, 1)]);
        Assert.Equal(800, run[^1].total);
    }

    [Fact]
    public void Le_profil_1LC_ne_recoit_jamais_le_1CC_d_une_partie()
    {
        var unLC = Profil("""{"ruleset":"1lc"}""");
        var unCC = Profil("""{"ruleset":"1cc"}""");
        var multi = Profil("""{"ruleset":"1cc-multi"}""");
        // Meme en tete de liste (version plus haute), le 1LC n'est pas le profil d'une partie seule.
        Assert.Equal("1cc", Regle(ModesDeJeu.ChoisirProfil([unLC, unCC, multi], null)));
        Assert.Equal("1lc", Regle(ModesDeJeu.ChoisirProfil1LC([unLC, unCC, multi], null)));
        Assert.Equal(["1cc"], ModesDeJeu.DuSolo([unLC, unCC, multi]).Select(p => Regle(p)).ToList());
    }

    [Fact]
    public void Un_jeu_sans_1LC_ouvert_ne_soumet_pas_de_1LC()
    {
        Assert.Null(ModesDeJeu.ChoisirProfil1LC([Profil("""{"ruleset":"1cc"}""")], null));
    }

    [Fact]
    public void Un_1LC_a_modes_suit_le_mode_joue()
    {
        var a = Profil("""{"ruleset":"1lc-original","mode":{"value":1,"default":true}}""");
        var b = Profil("""{"ruleset":"1lc-super","mode":{"value":2}}""");
        var cc = Profil("""{"ruleset":"1cc-original","mode":{"value":1,"default":true}}""");
        Assert.Equal("1lc-super", Regle(ModesDeJeu.ChoisirProfil1LC([a, b, cc], 2)));
        Assert.Equal("1lc-original", Regle(ModesDeJeu.ChoisirProfil1LC([a, b, cc], null)));
        Assert.Equal("1cc-original", Regle(ModesDeJeu.ChoisirProfil([a, b, cc], 1)));
    }

    [Fact]
    public void Le_1LC_se_rattache_au_replay_du_1CC()
    {
        Assert.Equal("rp_ABC", RetroBat.Api.Infrastructure.NelfePlayScoringReporter.ReplayDuLien("rp_ABC|1lc"));
        Assert.Equal("rp_ABC", RetroBat.Api.Infrastructure.NelfePlayScoringReporter.ReplayDuLien("rp_ABC"));
    }

    [Fact]
    public void Le_speedrun_n_est_jamais_le_profil_d_une_partie_seule()
    {
        var speedrun = Profil("""{"ruleset":"speedrun"}""");
        var unCC = Profil("""{"ruleset":"1cc"}""");
        Assert.Equal("1cc", Regle(ModesDeJeu.ChoisirProfil([speedrun, unCC], null)));
    }
}
