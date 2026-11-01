using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEROULEMENT D'UNE EPREUVE (lot P6) : ce que le bilan lit au journal, et ce que le verdict du site en fait. L'ecran et les
/// boutons se verifient sur la borne ; ici, les regles.
/// </summary>
public class DefisDeroulementTests
{
    private static readonly Reglement GreenHill1 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json")));

    private static DefisPanelService Service() => new(null!, null!, null!, null!, null!);

    private static string Tentative(string session, string etat, long? frames = null, string? cause = null, string check = "ok")
        => $$"""{"kind":"attempt","sessionId":"{{session}}","challengeId":"sonic-the-hedgehog.green-hill-1","state":"{{etat}}","cause":{{(cause is null ? "null" : "\"" + cause + "\"")}},"officialFrames":{{(frames?.ToString() ?? "null")}},"gameTimeCheck":"{{check}}"}""";

    [Fact]
    public void Le_bilan_d_une_session_compte_ses_tentatives_et_garde_le_meilleur_temps_classable()
    {
        string[] journal =
        [
            Tentative("s1", "FINISHED", 2900),
            """{"kind":"wrapper_summary","sessionId":"s1","summary":{}}""",
            Tentative("autre", "FINISHED", 2000),
            Tentative("s1", "INVALID", cause: "LOSE_LIFE"),
            Tentative("s1", "FINISHED", 2800, check: "ecart"),
            Tentative("s1", "FINISHED", 2850),
            Tentative("s1", "ABORTED", cause: "abandoned"),
        ];

        var bilan = SpeedrunSessionService.LireLeBilan(journal, "s1")!;

        Assert.Equal("sonic-the-hedgehog.green-hill-1", bilan.DefiId);
        Assert.Equal(5, bilan.Tentatives);
        Assert.Equal(3, bilan.Finies);
        Assert.Equal(2850, bilan.Meilleur);   // 2800 est contredit par le temps du jeu
        Assert.Equal(("ABORTED", "abandoned"), (bilan.DernierEtat, bilan.DerniereCause));
    }

    [Fact]
    public void Une_session_sans_tentative_n_a_pas_de_bilan()
        => Assert.Null(SpeedrunSessionService.LireLeBilan([Tentative("autre", "FINISHED", 2000), "pas du json"], "s1"));

    [Theory]
    [InlineData("published", IssueDuBilan.Classe)]
    [InlineData("held", IssueDuBilan.Retenu)]
    [InlineData("quarantined", IssueDuBilan.Retenu)]
    [InlineData("rejected", IssueDuBilan.Refuse)]
    [InlineData("", IssueDuBilan.EnVerification)]
    public void Le_statut_du_site_dit_le_sort_du_temps(string statut, IssueDuBilan issue)
        => Assert.Equal(issue, DefisPanelService.IssueDuStatut(statut));

    [Fact]
    public void Un_temps_envoye_attend_son_verdict_et_un_lancement_sale_ne_part_pas()
    {
        var session = new BilanDeSession("s1", GreenHill1.ChallengeId, 3, 1, 2900, "FINISHED", null);

        Assert.Equal(IssueDuBilan.EnVerification, Service().BilanDe(GreenHill1, "Sonic", null, session, sale: false).Issue);
        var sale = Service().BilanDe(GreenHill1, "Sonic", null, session, sale: true);
        Assert.Equal(IssueDuBilan.NonEnvoye, sale.Issue);
        Assert.NotEqual("", sale.Motif);
    }

    [Theory]
    [InlineData("INVALID", "LOSE_LIFE", "Last attempt: life lost")]
    [InlineData("INVALID", "PAUSE_ON", "Last attempt voided: pause")]
    [InlineData("INVALID", "LEVEL_CHEAT_ACTIVATED", "Last attempt voided: debug mode")]
    [InlineData("INVALID", "save_state_load", "Last attempt voided")]
    [InlineData("ABORTED", "abandoned", "Last attempt not finished")]
    public void Sans_temps_le_bilan_dit_comment_la_derniere_tentative_a_fini(string etat, string cause, string motif)
    {
        var bilan = Service().BilanDe(GreenHill1, "Sonic", null, new BilanDeSession("s1", GreenHill1.ChallengeId, 2, 0, null, etat, cause), false);

        Assert.Equal(IssueDuBilan.AucunTemps, bilan.Issue);
        Assert.Null(bilan.Temps);
        Assert.Equal(motif, bilan.Motif);
    }

    [Fact]
    public void Un_verdict_publie_apporte_le_rang_le_record_et_les_voisins()
    {
        var envoye = new BilanDEpreuve(GreenHill1, "Sonic", "Nelfe", null, IssueDuBilan.EnVerification, 2900, 3, 1);
        var classement = new ClassementDEpreuve(5, [new TempsClasse(1, "Tails", 2845), new TempsClasse(2, "Nelfe", 2900)]);

        var classe = DefisPanelService.AvecLeVerdict(envoye, IssueDuBilan.Classe, 2, 2900, classement);

        Assert.Equal((IssueDuBilan.Classe, 2, 2845L, 5), (classe.Issue, classe.Rang!.Value, classe.Record!.Value, classe.Classes!.Value));
        Assert.Equal(2, classe.Classement!.Count);
    }

    [Fact]
    public void Un_temps_refuse_n_a_ni_rang_ni_voisins()
    {
        var envoye = new BilanDEpreuve(GreenHill1, "Sonic", "Nelfe", null, IssueDuBilan.EnVerification, 2900, 3, 1);

        var refuse = DefisPanelService.AvecLeVerdict(envoye, IssueDuBilan.Refuse, 4, 2900, null);

        Assert.Null(refuse.Rang);
        Assert.Null(refuse.Classement);
    }

    [Theory]
    [InlineData(@"E:\RetroBat\roms\megadrive\Sonic The Hedgehog (USA, Europe).zip", "Sonic The Hedgehog")]
    [InlineData("E:/RetroBat/roms/arcade/mslug3.zip", "mslug3")]
    public void Le_nom_du_jeu_se_lit_sans_les_regions(string chemin, string nom)
        => Assert.Equal(nom, DefisPanelService.NomDuFichier(chemin));

    [Fact]
    public void La_preparation_donne_le_depart_quand_le_jeu_est_la()
    {
        var depart = DateTime.UtcNow;
        var page = Service().ComposerLaPreparation(GreenHill1, "Sonic", null, true, "fr", depart);

        Assert.Equal(depart, page.DepartUtc);
        Assert.Equal(2, page.Etape);
    }
}
