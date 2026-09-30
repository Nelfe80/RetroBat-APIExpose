using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le 1CC MULTI d'un joueur (2026-09-30) : chaque borne certifie son joueur, avec les compteurs
/// d'entrees de son port, de son depart a son propre continue.
/// </summary>
public class UnCreditMultiTests
{
    private const string Session = """
        {"type":"listener.session","frame_count":9000,"impossible_inputs":0,"press_count":410,"press_frames_sum":2000,
         "press_frames_sq":12000,"macro_repeats":1,"macro_windows":0,
         "ports":[{"port":0,"impossible_inputs":0,"press_count":410,"press_frames_sum":2000,"press_frames_sq":12000,"macro_repeats":1,"macro_windows":0},
                  {"port":1,"impossible_inputs":2,"press_count":388,"press_frames_sum":1900,"press_frames_sq":11000,"macro_repeats":2,"macro_windows":0}],
         "checkpoints":[]}
        """;

    [Fact]
    public void Le_proces_verbal_du_port_de_l_invite_porte_ses_compteurs()
    {
        var vu = System.Text.Json.Nodes.JsonNode.Parse(NelfePlayScoringReporter.SessionDuPort(Session, 1)!)!;

        Assert.Equal(388, (long)vu["press_count"]!);
        Assert.Equal(2, (long)vu["impossible_inputs"]!);
        Assert.Equal(2, (long)vu["macro_repeats"]!);
        Assert.Equal(9000, (long)vu["frame_count"]!);
    }

    [Fact]
    public void Un_port_sans_appui_ou_un_vieux_wrapper_ne_donne_rien()
    {
        Assert.Null(NelfePlayScoringReporter.SessionDuPort(Session, 2));
        Assert.Null(NelfePlayScoringReporter.SessionDuPort("""{"press_count":12}""", 0));
    }

    [Fact]
    public void Les_joueurs_actifs_se_comptent_aux_ports()
    {
        Assert.Equal(2, NelfePlayScoringReporter.JoueursActifs(Session));
        Assert.Equal(0, NelfePlayScoringReporter.JoueursActifs("""{"press_count":12}"""));
    }

    [Fact]
    public void L_invite_part_a_son_credit_et_seul_son_continue_le_coupe()
    {
        // L'hote est parti avant que l'invite n'arrive : l'invite voit d'abord des credits deja
        // consommes, puis le sien (START de son panel, frame 3000), puis le continue de l'hote (sans
        // START ici, frame 6000), puis le sien (START ici, frame 8000).
        var credits = new List<EvenementDeCredit> { new(3, 100), new(2, 3000), new(1, 6000), new(0, 8000) };
        var departs = new List<DepartDeJoueur> { new(1, 2990), new(1, 7995) };

        var bilan = ContinuesParCredits.Calculer(credits, departs, ouverteAuxJoueurs: true);

        Assert.Equal(3000L, bilan.Depart);
        Assert.Equal([8000L], bilan.Coupes);
        Assert.Contains(6000L, bilan.Arrivees);
    }

    [Fact]
    public void Le_depart_d_une_partie_seule_est_le_premier_credit()
    {
        var credits = new List<EvenementDeCredit> { new(2, 50), new(1, 900), new(0, 2640) };

        var bilan = ContinuesParCredits.Calculer(credits, []);

        Assert.Equal(900L, bilan.Depart);
        Assert.Equal([2640L], bilan.Coupes);
    }
}
