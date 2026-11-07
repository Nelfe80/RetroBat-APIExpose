using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES ARRIVEES DES NIVEAUX DE SONIC THE HEDGEHOG 2 vues au banc sans fenetre (07/11, verification avant ouverture). Chaque
/// trace part du carton de l'etat propose ; AIDES DE MESURE : un lampadaire ecrit pres de la fin (le niveau repart du carton,
/// chrono du jeu remis a 0:00 pendant le carton, la trace commence a ce carton), Sonic invincible pendant le boss, points de
/// vie du boss ramenes a 1 et Sonic pose sur le boss pour le toucher, puis pose au-dessus de la capsule. Le joueur automatique
/// court jusqu'au panneau. Image de la coupure du chrono (0xFE1F a 0) vue en planche (verif/s2/planche_*.png) : Sonic au
/// panneau, ou debout sur le bouton de la capsule apres la defaite du boss (score +1000). Le Death Egg part de l'etat sans
/// lampadaire et finit au passage en sequence de fin (0xF601 = 0x20).
/// </summary>
public class SpeedrunSonic2ArriveesTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string slug) => Reglement.Lire(File.ReadAllText(Fichier($"sonic-the-hedgehog-2.{slug}.json")));

    private static PassageMesure Jouer(string slug)
        => BancDesReplays.Mesurer(TraceDeReplay.Lire(File.ReadAllLines(Fichier($"sonic2-{slug}-arrivee.trace"))),
            LignesDuMem.Lire(File.ReadAllText(Fichier("sonic-the-hedgehog-2.MEM"))), [(slug, Lire(slug))])[0];

    // niveau, image du depart (0xF601 de 0x8C a 0x0C), image de la coupure du chrono (0xFE1F a 0, arrivee provisoire),
    // image de la confirmation (0xF7CD = 1 quand Sonic retombe apres le panneau, sinon la zone ou l'acte qui change),
    // images officielles = coupure - depart. Le Death Egg n'a pas de confirmation : arrivee = 0xF601 a 0x20.
    public static readonly TheoryData<string, long, long, long, long> Arrivees = new()
    {
        { "emerald-hill-1", 172, 259, 382, 87 }, { "emerald-hill-2", 171, 1227, 2613, 1056 },
        { "chemical-plant-1", 177, 350, 1534, 173 }, { "chemical-plant-2", 176, 1008, 2398, 832 },
        { "aquatic-ruin-1", 169, 446, 569, 277 }, { "aquatic-ruin-2", 173, 1107, 2501, 934 },
        { "casino-night-1", 177, 262, 385, 85 }, { "casino-night-2", 177, 962, 2360, 785 },
        { "hill-top-1", 195, 486, 609, 291 }, { "hill-top-2", 198, 1049, 2443, 851 },
        { "mystic-cave-1", 168, 346, 1530, 178 }, { "mystic-cave-2", 170, 1055, 2460, 885 },
        { "oil-ocean-1", 174, 260, 383, 86 }, { "oil-ocean-2", 173, 892, 2359, 719 },
        { "metropolis-1", 184, 366, 1550, 182 }, { "metropolis-2", 186, 368, 1552, 182 },
        { "metropolis-3", 182, 998, 2400, 816 },
        { "death-egg", 60, 5396, 5396, 5336 },
    };

    [Theory]
    [MemberData(nameof(Arrivees))]
    public void L_arrivee_est_la_fin_reelle_du_niveau(string slug, long depart, long coupure, long confirmation, long frames)
    {
        var p = Jouer(slug);
        Assert.Equal(EtatTentative.Finie, p.Resultat.Etat);
        Assert.Equal(depart, p.Depart);
        Assert.Equal(frames, p.Resultat.FramesOfficielles);
        Assert.Equal(coupure - depart, p.Resultat.FramesOfficielles);
        Assert.Equal(confirmation, p.Resultat.FrameDeFin);
    }

    [Fact]
    public void Metropolis_3_est_la_zone_5_acte_0()
    {
        // metropolis_zone_act_3 = (metropolis_zone_2 << 8) | 0 (s2disasm) : au banc, la fin de Metropolis 2 mene en zone 0x05
        // acte 0, la capsule de Metropolis 3 en zone 0x10 (Sky Chase) acte 0.
        var r = Lire("metropolis-3");
        Assert.Contains(r.Depart, c => c.Adresse == 0xFE11 && c.Egal == 0x05);
        Assert.Contains(r.Depart, c => c.Adresse == 0xFE10 && c.Egal == 0x00);
    }
}
