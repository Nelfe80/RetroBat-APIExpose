using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// METAL SLUG 3 AU COUTEAU v7 (08/11, decision du user apres un rapport de support) : monter dans un vehicule est permis. La
/// v6 echouait des que 0x1002C1 quittait 01 (a pied) ; un joueur pris dans le Metal Slug avant Huge Hermit perdait sa
/// tentative sans que le resume le dise. Les tirs a bord ne montent pas le compteur 0x100590 (16 replays de la borne rejoues
/// dans FBNeo le 08/11 : 48 montees et descentes de tank ou de Slug Mariner, aucun tir compte a bord), donc seul l'echec
/// « vehicule » part ; un tir d'arme a pied fait toujours echouer.
/// </summary>
public class DefiCouteauV7Tests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom)));

    private static int _ids;

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    /// <summary>Le depart, le Metal Slug pris puis quitte (0x04, comme au rejeu : 01 -> 04 -> 01 -> 04 a la montee), la fin de la mission 1.</summary>
    private static string Jouer(Reglement r, bool tirAPied = false)
    {
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "v" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        s.RecevoirImage([S(800, "GAME_START", "0x10FDB6", 0x01)]);
        s.RecevoirImage([S(803, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
        s.RecevoirImage([S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
        s.RecevoirImage([S(1700, "MOUNT_STATE", "0x1002C1", 0x01)]);
        s.RecevoirImage([S(9800, "MOUNT_STATE", "0x1002C1", 0x04)]);
        s.RecevoirImage([S(9824, "MOUNT_STATE", "0x1002C1", 0x01)]);
        s.RecevoirImage([S(9826, "MOUNT_STATE", "0x1002C1", 0x04)]);
        s.RecevoirImage([S(11000, "PROGRESSION_STAGE", "0x10040D", 0x03)]);
        s.RecevoirImage([S(12500, "MOUNT_STATE", "0x1002C1", 0x01)]);
        if (tirAPied)
        {
            s.RecevoirImage([S(12600, "AMMO_LOSE", "0x100590", 0x01)]);
        }

        s.RecevoirImage([S(13000, "PROGRESSION_STAGE", "0x10040D", 0x04)]);
        s.RecevoirImage([S(16000, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
        s.RecevoirImage([S(16200, "PROGRESSION_STAGE", "0x10040C", 0x01), S(16200, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
        return s.Etat;
    }

    [Fact]
    public void La_v7_finit_avec_le_Metal_Slug()
    {
        var v7 = Lire("metal-slug-3.mission-1-knife.v7.json");
        Assert.Equal(("ch-ms3-m1-knife-v7", 7), (v7.Ruleset, v7.Version));
        Assert.Equal("FINISHED", Jouer(v7));
    }

    [Fact]
    public void La_v6_perdait_cette_tentative()
        => Assert.Equal("INVALID", Jouer(Lire("metal-slug-3.mission-1-knife.v6.json")));

    [Fact]
    public void Un_tir_a_pied_fait_toujours_echouer()
        => Assert.Equal("INVALID", Jouer(Lire("metal-slug-3.mission-1-knife.v7.json"), tirAPied: true));

    [Fact]
    public void La_v7_garde_tout_le_reste_de_la_v6()
    {
        var v6 = Lire("metal-slug-3.mission-1-knife.v6.json");
        var v7 = Lire("metal-slug-3.mission-1-knife.v7.json");
        Assert.Equal(v6.LignesDesVerrous, v7.LignesDesVerrous);
        Assert.Equal(v6.Compteurs.Count, v7.Compteurs.Count);
        Assert.Equal(v6.Echecs.Count - 1, v7.Echecs.Count);
        Assert.DoesNotContain(v7.Echecs, e => e.Adresse == 0x1002C1);
        Assert.Empty(v7.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.v5.MEM"))));
    }
}
