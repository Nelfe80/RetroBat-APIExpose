using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// CHAQUE CLASSEMENT A SA CAPTURE (decision du user du 23/11) : la borne demande le seuil de chaque regle solo du jeu
/// (1cc, 1lc, modes speciaux), pas seulement celui que le site prenait par defaut.
/// </summary>
public sealed class ScoreShotReglesTests
{
    private const string Profils = """
        {"ok":true,"open":true,
         "profile":{"ruleset":"1cc"},
         "profiles":[{"ruleset":"1cc"},{"ruleset":"1cc-extended"},{"ruleset":"1cc-special"},{"ruleset":"1cc-multi"},
                     {"ruleset":"1lc"},{"ruleset":"1lc-power-up"},{"ruleset":"1lc-multi"},{"ruleset":"speedrun"},{"ruleset":"speedrun-any"},{"ruleset":"1cc"}]}
        """;

    [Fact]
    public void Les_regles_solo_au_score_se_photographient_1cc_1lc_et_modes_speciaux()
    {
        var regles = ScoreShotRegles.ReglesPhotographiables(Profils);
        Assert.Equal(new[] { "1cc", "1cc-extended", "1cc-special", "1lc", "1lc-power-up" }, regles);
    }

    [Fact]
    public void Ni_le_speedrun_ni_le_multi_ni_un_jeu_ferme_ne_se_photographient()
    {
        Assert.Empty(ScoreShotRegles.ReglesPhotographiables("{\"ok\":true,\"open\":false}"));
        Assert.Empty(ScoreShotRegles.ReglesPhotographiables("pas du json"));
        Assert.Empty(ScoreShotRegles.ReglesPhotographiables("{\"open\":true,\"profiles\":[{\"ruleset\":\"1cc-multi\"},{\"ruleset\":\"speedrun\"}]}"));
    }

    [Fact]
    public void Un_vieux_site_qui_ne_rend_que_profile_donne_sa_regle()
    {
        Assert.Equal(new[] { "1cc" }, ScoreShotRegles.ReglesPhotographiables("{\"open\":true,\"profile\":{\"ruleset\":\"1cc\"}}"));
    }

    [Theory]
    [InlineData(null, false, 1, true)]      // tableau pas plein : tout score positif entre
    [InlineData(null, false, 0, false)]
    [InlineData(1000L, false, 1001, true)]
    [InlineData(1000L, false, 1000, false)]
    [InlineData(1000L, true, 999, true)]    // lower_better : sous le plafond
    [InlineData(1000L, true, 1000, false)]
    public void Le_seuil_se_franchit_dans_le_sens_du_classement(long? seuil, bool bas, long total, bool attendu)
        => Assert.Equal(attendu, ScoreShotRegles.Franchit(seuil, bas, total));

    [Fact]
    public void Le_nom_de_fichier_d_une_regle_ne_peut_jamais_etre_un_chemin()
    {
        Assert.Equal("1cc-extended", ScoreShotRegles.NomDeFichier("1CC-Extended"));
        Assert.DoesNotContain("..", ScoreShotRegles.NomDeFichier("../../x"));
        Assert.DoesNotContain("/", ScoreShotRegles.NomDeFichier("a/b\\c"));
    }
}

/// <summary>Le rattrapage des records deja tenus (23/11) se fait jeu par jeu, regle par regle, sans refaire les bonnes images.</summary>
public sealed class ScoreShotRattrapageFiltreTests
{
    private static ScoreShotRegenerator.Candidat C(string jeu, string regle, bool image)
        => new("s", "r", jeu, regle, "arcade", 100, 10, image);

    [Fact]
    public void Le_filtre_choisit_un_jeu_une_regle_et_les_classements_sans_image()
    {
        var tous = new[] { C("blazing-star", "1cc", true), C("blazing-star", "1lc", false), C("esp-ra-de", "1lc", false) };
        Assert.Equal(3, tous.Count(ScoreShotRegenerator.Filtre.Aucun.Retient));
        Assert.Single(tous.Where(new ScoreShotRegenerator.Filtre("blazing-star", "1lc").Retient));
        Assert.Equal(2, tous.Count(new ScoreShotRegenerator.Filtre(null, "1lc").Retient));
        Assert.Equal(2, tous.Count(new ScoreShotRegenerator.Filtre(null, null, true).Retient));
        Assert.Empty(tous.Where(new ScoreShotRegenerator.Filtre("blazing-star", "1cc", true).Retient));
    }
}
