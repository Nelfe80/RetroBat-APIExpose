using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPEEDRUNS DE SONIC PAR NIVEAU ET LE JEU COMPLET (demande du user du 02/11, grille du prototype Sonic §5.1 et §5.4).
/// 19 niveaux en depart direct (Green Hill 1 v2, deja ouvert, et les 18 autres), chacun sur l'etat du panneau de son niveau
/// fabrique au labo (zone et acte ecrits pendant Green Hill 1, niveau relance, chrono coupe comme apres une mort). L'Any% part
/// d'une partie neuve et finit a la sequence de fin (0xF601 = 0x18), morts et continues permis.
/// </summary>
public class SpeedrunSonicNiveauxTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"sonic-the-hedgehog.{slug}.json")));

    /// <summary>Les 19 niveaux dans l'ordre du jeu : slug, zone (0xFE11), acte (0xFE10).</summary>
    public static readonly TheoryData<string, int, int> Niveaux = new()
    {
        { "green-hill-1.v2", 0, 0 }, { "green-hill-2", 0, 1 }, { "green-hill-3", 0, 2 },
        { "marble-1", 2, 0 }, { "marble-2", 2, 1 }, { "marble-3", 2, 2 },
        { "spring-yard-1", 4, 0 }, { "spring-yard-2", 4, 1 }, { "spring-yard-3", 4, 2 },
        { "labyrinth-1", 1, 0 }, { "labyrinth-2", 1, 1 }, { "labyrinth-3", 1, 2 },
        { "star-light-1", 3, 0 }, { "star-light-2", 3, 1 }, { "star-light-3", 3, 2 },
        { "scrap-brain-1", 5, 0 }, { "scrap-brain-2", 5, 1 }, { "scrap-brain-3", 1, 3 },
        { "final-zone", 5, 2 },
    };

    /// <summary>Les 18 niveaux qui finissent sur la fin d'acte ; Final Zone finit a la sequence de fin.</summary>
    public static readonly TheoryData<string, int, int> NiveauxAFinDActe = new();

    static SpeedrunSonicNiveauxTests()
    {
        foreach (var n in Niveaux)
        {
            if ((string)n[0] != "final-zone") NiveauxAFinDActe.Add((string)n[0], (int)n[1], (int)n[2]);
        }
    }

    private static int _ids;

    private static SessionSpeedrun Nouvelle(Reglement r) => new(r.ChallengeId, r,
        () => "id" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.922743));

    private static void Image(SessionSpeedrun s, params Signal[] signaux) => s.RecevoirImage(signaux);

    private static void Relever(SessionSpeedrun s, IReadOnlyDictionary<long, int> valeurs)
    {
        foreach (var a in s.AdressesInconnues) s.Relever(a, valeurs.GetValueOrDefault(a));
    }

    /// <summary>L'etat du panneau charge : niveau connu, chrono coupe, puis le jeu le relance et rend la main.</summary>
    private static void PartirDuPanneau(SessionSpeedrun s, int zone, int acte)
    {
        Relever(s, new Dictionary<long, int> { [0xFE11] = zone, [0xFE10] = acte, [0xF601] = 0x8C });
        Image(s, S(160, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(185, "GAME_PLAYING", "0x00F601", 0x0C));
    }

    [Fact]
    public void Les_19_niveaux_ont_chacun_leur_classement_dans_l_ordre_du_jeu()
    {
        var reglements = Niveaux.Select(n => Lire((string)n[0])).ToList();
        Assert.Equal(19, reglements.Select(r => r.Ruleset).Distinct().Count());
        Assert.Equal(19, reglements.Select(r => r.ChallengeId).Distinct().Count());
        Assert.All(reglements, r => Assert.Equal("speedrun_level", r.Kind));
        Assert.All(reglements, r => Assert.Equal("canonical_state", r.StartPolicy));
        Assert.Equal(Enumerable.Range(0, 19).Select(i => i == 0 ? 0 : i + 1), reglements.Select(r => r.Ordre));
        Assert.Equal(19, reglements.Select(r => r.Etat!.Sha256).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Niveaux))]
    public void Chaque_niveau_cite_seulement_des_lignes_du_MEM_et_part_de_son_etat(string slug, int zone, int acte)
    {
        var r = Lire(slug);
        Assert.Empty(r.VerifierContre(File.ReadAllBytes(Fichier("sonic-the-hedgehog.MEM"))));
        Assert.NotNull(r.Etat);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFE11 && c.Egal == zone);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFE10 && c.Egal == acte);
        Assert.StartsWith("megadrive/sonic-the-hedgehog/", r.Etat!.Fichier);
    }

    [Theory]
    [MemberData(nameof(NiveauxAFinDActe))]
    public void Le_panneau_le_chrono_puis_la_fin_d_acte_finissent_la_course(string slug, int zone, int acte)
    {
        var s = Nouvelle(Lire(slug));
        PartirDuPanneau(s, zone, acte);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);

        for (var sec = 1; sec <= 40; sec++) Image(s, S(160 + 60 * sec, "LEVEL_TIMER", "0x00FE25", sec));
        Image(s, S(2600, "GENERAL_TIMER", "0x00FE1F", 0));
        Relever(s, new Dictionary<long, int>());
        Image(s, S(2724, "LEVEL_CLEAR", "0x00F7CD", 1));

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Theory]
    [MemberData(nameof(Niveaux))]
    public void Une_mort_fait_echouer_le_niveau(string slug, int zone, int acte)
    {
        var s = Nouvelle(Lire(slug));
        PartirDuPanneau(s, zone, acte);
        Image(s, S(900, "GENERAL_TIMER", "0x00FE1F", 0), S(900, "LOSE_LIFE", "0x00FE13", 2));

        Assert.Equal(SessionSpeedrun.Invalid, s.Etat);
    }

    /// <summary>
    /// Scrap Brain 2 et 3 passent au niveau suivant par une relance du jeu, qui ne coupe pas le chrono (labo du 02/11) :
    /// l'arrivee est le changement de zone, Labyrinth acte 4 (Scrap Brain 3) puis Final Zone.
    /// </summary>
    [Theory]
    [InlineData("scrap-brain-2", 5, 1, 1, 3)]
    [InlineData("scrap-brain-3", 1, 3, 5, 2)]
    public void Scrap_Brain_2_et_3_finissent_au_changement_de_zone(string slug, int zone, int acte, int zoneSuivante, int acteSuivant)
    {
        var s = Nouvelle(Lire(slug));
        PartirDuPanneau(s, zone, acte);
        Relever(s, new Dictionary<long, int>());
        Image(s, S(4100, "PROGRESSION_ZONE", "0x00FE11", zoneSuivante), S(4100, "PROGRESSION_STAGE", "0x00FE10", acteSuivant));

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void Final_Zone_finit_a_la_sequence_de_fin()
    {
        var s = Nouvelle(Lire("final-zone"));
        PartirDuPanneau(s, 5, 2);
        Relever(s, new Dictionary<long, int>());
        Image(s, S(5000, "GAME_OVER", "0x00F601", 0x18));

        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void L_Any_pourcent_part_de_Green_Hill_1_et_finit_a_la_sequence_de_fin_malgre_les_morts()
    {
        var any = Lire("any");
        Assert.Equal("speedrun_full", any.Kind);
        Assert.Equal("normal_boot", any.StartPolicy);
        Assert.Empty(any.VerifierContre(File.ReadAllBytes(Fichier("sonic-the-hedgehog.MEM"))));

        var s = Nouvelle(any);
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        Relever(s, new Dictionary<long, int>());
        Image(s, S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(909, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(934, "GAME_PLAYING", "0x00F601", 0x0C));
        Assert.Equal(SessionSpeedrun.Running, s.Etat);

        // Fin de Green Hill 1, Green Hill 2, une mort, un continue, Marble 1 : rien n'arrete le jeu complet.
        Image(s, S(3754, "GENERAL_TIMER", "0x00FE1F", 0));
        Image(s, S(3878, "LEVEL_CLEAR", "0x00F7CD", 1));
        Image(s, S(4200, "PROGRESSION_STAGE", "0x00FE10", 1));
        Image(s, S(4300, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(4304, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(6000, "GENERAL_TIMER", "0x00FE1F", 0), S(6000, "LOSE_LIFE", "0x00FE13", 2));
        Image(s, S(6500, "CONTINUE_SCREEN", "0x00F601", 0x14));
        Image(s, S(7000, "PROGRESSION_ZONE", "0x00FE11", 2));
        Assert.Equal(SessionSpeedrun.Running, s.Etat);

        Image(s, S(90000, "GAME_OVER", "0x00F601", 0x18));
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
    }

    [Fact]
    public void L_Any_pourcent_revenu_a_l_ecran_titre_est_annule()
    {
        var s = Nouvelle(Lire("any"));
        Image(s, S(500, "TITLE_SCREEN", "0x00F601", 0x04));
        Relever(s, new Dictionary<long, int>());
        Image(s, S(905, "GAME_PLAYING", "0x00F601", 0x8C));
        Image(s, S(909, "GENERAL_TIMER", "0x00FE1F", 1));
        Image(s, S(934, "GAME_PLAYING", "0x00F601", 0x0C));
        Image(s, S(20000, "TITLE_SCREEN", "0x00F601", 0x04));

        Assert.NotEqual(SessionSpeedrun.Running, s.Etat);
        Assert.NotEqual(SessionSpeedrun.Finished, s.Etat);
    }
}
