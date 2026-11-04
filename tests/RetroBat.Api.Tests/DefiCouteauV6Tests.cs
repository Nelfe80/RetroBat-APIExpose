using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// METAL SLUG 3 FACILITE v6 (04/11, rapport d'AshTaste92) : la v5 arrivait en entrant dans la section 4 de la mission 1
/// (0x10040D = 4), que l'autre chemin ne traverse pas ; la mission 2 commencait, l'annulation « la mission change » tombait,
/// et la tentative etait perdue. La v6 arrive a la fin de la mission 1 (0x10040C quitte 0), quel que soit le chemin.
/// </summary>
public class DefiCouteauV6Tests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom)));

    private static int _ids;

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    /// <summary>Le depart, puis le chemin de la mer (sections 1, 2, 3, 5 : jamais 4), puis la fin de la mission 1.</summary>
    private static string Jouer(Reglement r)
    {
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "v" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        s.RecevoirImage([S(800, "GAME_START", "0x10FDB6", 0x01)]);
        s.RecevoirImage([S(803, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
        s.RecevoirImage([S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
        s.RecevoirImage([S(1700, "MOUNT_STATE", "0x1002C1", 0x01)]);
        var frame = 2000L;
        foreach (var section in new[] { 1, 2, 3, 5 })
        {
            s.RecevoirImage([S(frame, "PROGRESSION_STAGE", "0x10040D", section)]);
            frame += 2000;
        }

        s.RecevoirImage([S(frame, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
        s.RecevoirImage([S(frame + 200, "PROGRESSION_STAGE", "0x10040C", 0x01), S(frame + 200, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
        return s.Etat;
    }

    [Fact]
    public void La_v6_arrive_a_la_fin_de_la_mission_par_l_autre_chemin()
    {
        var v6 = Lire("metal-slug-3.mission-1-knife.v6.json");
        Assert.Equal("ch-ms3-m1-knife-v6", v6.Ruleset);
        Assert.Equal(6, v6.Version);
        Assert.DoesNotContain(v6.AnnulationsJeu, e => e.Adresse == 0x10040C);
        Assert.Equal("FINISHED", Jouer(v6));
    }

    [Fact]
    public void La_v5_perdait_cette_tentative()
        => Assert.Equal("INVALID", Jouer(Lire("metal-slug-3.mission-1-knife.v5.json")));

    [Fact]
    public void La_v6_garde_les_verrous_et_les_coups_de_la_v5()
    {
        var v5 = Lire("metal-slug-3.mission-1-knife.v5.json");
        var v6 = Lire("metal-slug-3.mission-1-knife.v6.json");
        Assert.Equal(v5.LignesDesVerrous, v6.LignesDesVerrous);
        Assert.Equal(v5.Compteurs.Count, v6.Compteurs.Count);
        Assert.Equal(v5.Etat, v6.Etat);
    }
}
