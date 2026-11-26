using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE « when » DE L'ARRIVEE (CDC normalisation, lot 4, 26/11). Super Mario Bros. : 0x0770 = 2 tombe a chaque chateau, la
/// communaute n'arrete le chrono qu'a la hache du monde 8 (monde >= 7). Une arrivee avec « when » ne vaut que si ses
/// conditions sont vraies ; sans « when », rien ne change.
/// </summary>
public class SpeedrunQuandArriveeTests
{
    private static string Json(string quandArrivee) => """
    {"schema":"nelfe.challenge.v2","challenge_id":"t.any","version":1,"ruleset":"t","kind":"speedrun_full",
     "game":{"system_id":"nes","rom_group":"t"},"start_policy":"normal_boot",
     "start":[{"value":{"address":"0x0770","equals":"0x01"}}],
     "timing":{"official":"independent","independent":{
       "start":{"event":{"action":"GAME_PLAYING","address":"0x000E","value":"0x08"}},
       "finish":{"event":{"action":"LEVEL_CLEAR","address":"0x0770","value":"0x02"}__QUAND__}}},
     "ranking":{"metric":"frames","direction":"lower_better"},"cores":["fceumm"]}
    """.Replace("__QUAND__", quandArrivee);

    private static Evaluateur Armer(string quand)
    {
        var ev = new Evaluateur(Reglement.Lire(Json(quand)));
        ev.Relever(0x0770, 1);
        ev.Relever(0x075F, 0);
        ev.Recevoir(Signal.Depuis(10, "GAME_PLAYING", "0x000E", 8));
        return ev;
    }

    [Fact]
    public void Un_chateau_de_monde_2_ne_finit_pas_un_any_qui_exige_le_monde_8()
    {
        var ev = Armer(""","when":[{"value":{"address":"0x075F","at_least":"0x07"}}]""");
        ev.Recevoir(Signal.Depuis(500, "PROGRESSION_ZONE", "0x075F", 1));
        ev.Recevoir(Signal.Depuis(600, "LEVEL_CLEAR", "0x0770", 2));
        Assert.Equal(EtatTentative.EnCourse, ev.Etat);
        ev.Recevoir(Signal.Depuis(5000, "PROGRESSION_ZONE", "0x075F", 7));
        ev.Recevoir(Signal.Depuis(6000, "LEVEL_CLEAR", "0x0770", 2));
        Assert.Equal(EtatTentative.Finie, ev.Etat);
        Assert.Equal(6000, ev.Resultat().FrameDeFin);
    }

    [Fact]
    public void Sans_when_l_arrivee_reste_celle_d_avant()
    {
        var ev = Armer("");
        ev.Recevoir(Signal.Depuis(600, "LEVEL_CLEAR", "0x0770", 2));
        Assert.Equal(EtatTentative.Finie, ev.Etat);
    }

    [Fact]
    public void Le_when_de_l_arrivee_est_une_adresse_citee_donc_surveillee()
    {
        var r = Reglement.Lire(Json(""","when":[{"value":{"address":"0x075F","at_least":"0x07"}}]"""));
        Assert.Contains(0x075FL, r.AdressesCitees());
    }
}
