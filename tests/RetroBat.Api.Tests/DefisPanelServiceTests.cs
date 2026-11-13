using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE PANNEAU DES DEFIS, sa composition (lot P5) : les onglets ne naissent que de leurs epreuves, les groupes suivent le
/// genre, et le journal local dit ce qui a ete tente ou reussi. Le dessin est couvert par les apercus, la navigation par
/// le modele.
/// </summary>
public class DefisPanelServiceTests
{
    private static readonly Reglement GreenHill1 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json")));

    private static DefisPanelService Service() => new(null!, null!, null!, null!, null!);

    [Fact]
    public void Un_speedrun_de_niveau_va_dans_SPEEDRUNS_sous_le_groupe_des_niveaux_avec_son_titre()
    {
        var lignes = Service().Composer([GreenHill1], new Dictionary<string, (int, long?)>(), "fr");

        var speedruns = Assert.Single(lignes).Value;
        Assert.Equal(DefisPanelModel.Onglet.Speedruns, Assert.Single(lignes).Key);
        Assert.True(speedruns[0].Ligne.Groupe);
        Assert.Equal("Levels", speedruns[0].Ligne.Nom);
        Assert.Equal("Green Hill Zone · Acte 1", speedruns[1].Ligne.Nom);
        Assert.Same(GreenHill1, speedruns[1].Defi);
        Assert.Equal("", speedruns[1].Ligne.Marque);
    }

    [Fact]
    public void Le_titre_suit_la_langue_et_retombe_sur_l_anglais()
    {
        Assert.Equal("Green Hill Zone · Act 1", GreenHill1.Titre("de"));
        Assert.Equal("Green Hill Zone · Acte 1", GreenHill1.Titre("fr_FR"));
    }

    [Fact]
    public void Le_journal_marque_une_epreuve_tentee_puis_reussie_avec_le_temps_du_joueur()
    {
        var tentee = Service().Composer([GreenHill1], new Dictionary<string, (int, long?)> { [GreenHill1.ChallengeId] = (3, null) }, "en");
        Assert.Equal("tente", tentee[DefisPanelModel.Onglet.Speedruns][1].Ligne.Marque);

        var reussie = Service().Composer([GreenHill1], new Dictionary<string, (int, long?)> { [GreenHill1.ChallengeId] = (4, 2845) }, "en");
        var ligne = reussie[DefisPanelModel.Onglet.Speedruns][1].Ligne;
        Assert.Equal("reussi", ligne.Marque);
        Assert.Equal("0:47.48", ligne.Colonne2);
    }

    /// <summary>Metal Slug 3 (13/11) : les challenges d'abord, puis le Boss Rush dans son groupe, ses boss dans l'ordre du jeu.</summary>
    [Fact]
    public void Le_Boss_Rush_a_son_groupe_sous_les_challenges()
    {
        static Reglement L(string f) => Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", f)));
        var lignes = Service().Composer(
            [L("metal-slug-3.boss-rootmars.json"), L("metal-slug-3.mission-1-rambo.json"), L("metal-slug-3.boss-m1.json"),
             L("metal-slug-3.mission-1-knife.v7.json")],
            new Dictionary<string, (int, long?)>(), "en");

        var onglet = Assert.Single(lignes);
        Assert.Equal(DefisPanelModel.Onglet.Challenges, onglet.Key);
        Assert.Equal(["Challenges", "My ### and my knife", "Rambo", "Boss Rush", "Mission 1: Huge Hermit", "Final mission: Rootmars"],
            onglet.Value.Select(l => l.Ligne.Nom));
    }

    [Fact]
    public void Sans_defi_aucun_onglet_n_existe()
        => Assert.Empty(Service().Composer([], new Dictionary<string, (int, long?)>(), "en"));
}
