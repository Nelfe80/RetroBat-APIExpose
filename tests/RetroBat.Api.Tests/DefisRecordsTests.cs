using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// RECORDS et les colonnes de record (CDC du panneau, §7.5) : ce que le site rend, ce que la liste et la fiche en montrent, et
/// les types qui n'existent que s'ils ont un temps a montrer.
/// </summary>
public class DefisRecordsTests
{
    private static readonly string Modele = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json"));

    private static readonly Reglement GreenHill1 = Reglement.Lire(Modele);

    private static readonly Dictionary<string, (int, long?)> SansBilan = new();

    private static Reglement Autre(string nom, string genre) => Reglement.Lire(Modele
        .Replace("sonic-the-hedgehog.green-hill-1", "sonic-the-hedgehog." + nom)
        .Replace("ch-sonic-green-hill-1-v1", "ch-" + nom)
        .Replace("speedrun_level", genre));

    private static DefisPanelService Service() => new(null!, null!, null!, null!, null!);

    private static Dictionary<string, ClassementDEpreuve> Tableaux(params (Reglement Defi, (string Joueur, long Valeur)[] Temps)[] tableaux)
        => tableaux.ToDictionary(t => t.Defi.Ruleset,
            t => new ClassementDEpreuve(t.Temps.Length, t.Temps.Select((l, i) => new TempsClasse(i + 1, l.Joueur, l.Valeur)).ToList()));

    [Fact]
    public void Le_classement_du_site_se_lit_ligne_par_ligne()
    {
        const string corps = """
            {"ok":true,"ruleset":"ch-sonic-green-hill-1-v1","total":2,"rows":[
              {"account":"a1","player":"Nelfe","value":2845,"time":"0:47.48"},
              {"account":"a2","player":"Tails","value":2900,"time":"0:48.40"}]}
            """;

        var classement = ClassementsDesDefis.Lire(corps);

        Assert.Equal(2, classement.Total);
        Assert.Equal(new TempsClasse(1, "Nelfe", 2845), classement.Lignes[0]);
        Assert.Equal(new TempsClasse(2, "Tails", 2900), classement.Lignes[1]);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"total\":0,\"rows\":[]}")]
    [InlineData("{\"ok\":false,\"error\":\"challenge_not_found\"}")]
    public void Un_classement_sans_ligne_reste_vide(string corps)
        => Assert.Empty(ClassementsDesDefis.Lire(corps).Lignes);

    [Fact]
    public void La_liste_montre_le_record_et_le_temps_du_joueur_lus_sur_le_site()
    {
        var tableaux = Tableaux((GreenHill1, [("Tails", 2845), ("Nelfe", 2900)]));

        var ligne = Service().Composer([GreenHill1], SansBilan, "fr", tableaux, "nelfe")[DefisPanelModel.Onglet.Speedruns][1].Ligne;

        Assert.Equal(Chronometrage.Afficher(2845, GreenHill1.Cadence), ligne.Colonne1);
        Assert.Equal(Chronometrage.Afficher(2900, GreenHill1.Cadence), ligne.Colonne2);
    }

    [Fact]
    public void Sans_temps_sur_le_site_le_joueur_garde_son_meilleur_temps_local()
    {
        var bilan = new Dictionary<string, (int, long?)> { [GreenHill1.ChallengeId] = (3, 3000) };

        var ligne = Service().Composer([GreenHill1], bilan, "fr", Tableaux((GreenHill1, [("Tails", 2845)])), "nelfe")[DefisPanelModel.Onglet.Speedruns][1].Ligne;

        Assert.Equal(Chronometrage.Afficher(3000, GreenHill1.Cadence), ligne.Colonne2);
    }

    [Fact]
    public void Sans_aucun_temps_RECORDS_n_a_aucun_type()
    {
        Assert.Empty(Service().ComposerLesRecords([GreenHill1], new Dictionary<string, ClassementDEpreuve>(), "fr", "nelfe"));
        Assert.Empty(Service().ComposerLesRecords([GreenHill1], Tableaux((GreenHill1, [])), "fr", "nelfe"));
    }

    [Fact]
    public void Les_types_suivent_les_epreuves_qui_ont_un_temps()
    {
        var marble = Autre("marble-1", "speedrun_level");
        var boss = Autre("boss-1", "speedrun_boss");
        var complet = Autre("any", "speedrun_full");

        var types = Service().ComposerLesRecords([GreenHill1, marble, boss, complet],
            Tableaux((GreenHill1, [("Nelfe", 2845)]), (marble, [("Tails", 4000)]), (complet, [("Nelfe", 90000)]), (boss, [])), "fr", "");

        Assert.Equal(3, types.Count);
        Assert.Equal("GLOBAL", types[0].Etiquette);
        Assert.Single(types[1].Vues);                      // le jeu complet : son propre type, une seule vue
        Assert.Equal("LEVELS", types[2].Etiquette);
        Assert.Equal(2, types[2].Vues.Count);              // LB et RB passent de Green Hill a Marble
    }

    [Fact]
    public void GLOBAL_classe_les_joueurs_par_records_detenus_et_compte_les_challenges()
    {
        var marble = Autre("marble-1", "speedrun_level");
        var defi = Autre("sans-anneau", "challenge_rule");

        var global = Service().ComposerLesRecords([GreenHill1, marble, defi],
            Tableaux((GreenHill1, [("Nelfe", 2845), ("Tails", 2900)]), (marble, [("Tails", 4000)]), (defi, [("Tails", 500)])), "fr", "tails")[0].Vues[0];

        Assert.Equal(["SPEEDRUNS", "CHALLENGES"], global.Colonnes);
        Assert.Equal("1   Tails", global.Lignes[0].Nom);
        Assert.Equal(("1", "1"), (global.Lignes[0].Colonne1, global.Lignes[0].Colonne2));
        Assert.Equal("2   Nelfe", global.Lignes[1].Nom);
        Assert.Equal(0, global.Epinglee);
    }

    [Fact]
    public void Les_challenges_n_ont_pas_de_type_a_eux()
    {
        var defi = Autre("sans-anneau", "challenge_rule");

        var types = Service().ComposerLesRecords([defi], Tableaux((defi, [("Tails", 500)])), "fr", "");

        Assert.Equal("GLOBAL", Assert.Single(types).Etiquette);
    }

    [Fact]
    public void Le_joueur_retrouve_sa_ligne_a_epingler_et_seulement_la_sienne()
    {
        var tableaux = Tableaux((GreenHill1, [("Tails", 2845), ("Nelfe", 2900)]));

        Assert.Equal(1, Service().ComposerLesRecords([GreenHill1], tableaux, "fr", "NELFE")[1].Vues[0].Epinglee);
        Assert.Null(Service().ComposerLesRecords([GreenHill1], tableaux, "fr", "Sonic")[1].Vues[0].Epinglee);
        Assert.Null(Service().ComposerLesRecords([GreenHill1], tableaux, "fr", "")[1].Vues[0].Epinglee);
    }
}
