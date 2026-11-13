using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE BOSS RUSH DE METAL SLUG 3 (challenge choisi par le user le 13/11) : une epreuve par boss, kind « challenge_boss » (note
/// Kinch a part des autres challenges du jeu). Chaque etat part de l'aire du boss, chargee par le jeu lui-meme (0x10ED7A
/// mission suivante, 0x10ED7B section suivante, posees pendant le SOLDIER SELECT) ; fabrique et verifie au banc sans fenetre
/// du 13/11 (coeur FBNeo de la borne, trois rechargements a froid identiques). Les images ci-dessous sont celles du banc :
/// top depart = le soldat pose (0x1002C1 quitte 00), arrivee = l'aire du boss qui se termine apres sa mort (touche D du
/// debug pour le tuer au banc).
/// </summary>
public class DefisMs3BossRushTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom)));

    private static int _ids;

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    public static IEnumerable<object[]> Boss() =>
    [
        // fichier, ruleset, mission, section, vehicule au top, image du top, adresse de l'arrivee, valeur d'arrivee, image d'arrivee
        ["metal-slug-3.boss-m1.json", "ch-ms3-boss-m1-v1", 0x00, 0x04, 0x01, 145, "0x10040C", 0x01, 1598],
        ["metal-slug-3.boss-m2.json", "ch-ms3-boss-m2-v1", 0x01, 0x07, 0x01, 118, "0x10040C", 0x02, 3137],
        ["metal-slug-3.boss-m3.json", "ch-ms3-boss-m3-v1", 0x02, 0x03, 0x01, 72, "0x10040C", 0x03, 1461],
        ["metal-slug-3.boss-m4.json", "ch-ms3-boss-m4-v1", 0x03, 0x02, 0x01, 96, "0x10040C", 0x04, 1666],
        ["metal-slug-3.boss-m5-fake.json", "ch-ms3-boss-m5-fake-v1", 0x04, 0x09, 0x01, 74, "0x10040D", 0x0A, 1248],
        ["metal-slug-3.boss-rootmars.json", "ch-ms3-boss-rootmars-v1", 0x04, 0x0E, 0x04, 161, "0x10040C", 0x05, 1862],
    ];

    private static SessionSpeedrun Partir(Reglement r, int mission, int section, int vehicule, long top)
    {
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "b" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        s.RecevoirImage([S(1, "PROGRESSION_STAGE", "0x10040D", section)]);
        foreach (var a in s.AdressesInconnues)
            s.Relever(a, a switch { 0x10FDB6 => 1, 0x10040C => mission, 0x10040D => section, _ => 0 });
        s.RecevoirImage([S(top, "MOUNT_STATE", "0x1002C1", vehicule)]);
        return s;
    }

    [Theory]
    [MemberData(nameof(Boss))]
    public void Chaque_boss_part_de_son_aire_et_finit_quand_l_aire_se_termine(string fichier, string ruleset, int mission, int section,
        int vehicule, long top, string adresse, int valeur, long arrivee)
    {
        var r = Lire(fichier);
        Assert.Equal((ruleset, "challenge_boss", 1), (r.Ruleset, r.Kind, r.Version));
        Assert.True(r.EstUnChallenge);
        Assert.Empty(r.Verrous);
        Assert.Empty(r.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.v5.MEM"))));
        var s = Partir(r, mission, section, vehicule, top);
        Assert.Equal(SessionSpeedrun.Running, s.Etat);
        s.RecevoirImage([S(arrivee, "PROGRESSION_STAGE", adresse, valeur)]);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        Assert.Equal(arrivee - top, s.PrendreLaMeilleure()!.Resultat.FramesOfficielles);
    }

    [Theory]
    [MemberData(nameof(Boss))]
    public void Une_mort_ne_fait_pas_echouer_un_continue_si(string fichier, string ruleset, int mission, int section,
        int vehicule, long top, string adresse, int valeur, long arrivee)
    {
        Assert.NotEmpty(ruleset);
        var r = Lire(fichier);
        var s = Partir(r, mission, section, vehicule, top);
        s.RecevoirImage([S(top + 300, "LOSE_LIFE", "0x1002BB", 0x01)]);
        s.RecevoirImage([S(arrivee, "PROGRESSION_STAGE", adresse, valeur)]);
        Assert.Equal(SessionSpeedrun.Finished, s.Etat);
        var c = Partir(r, mission, section, vehicule, top);
        c.RecevoirImage([S(top + 300, "CONTINUE_SCREEN", "0x10FDB6", 0x02)]);
        c.RecevoirImage([S(arrivee, "PROGRESSION_STAGE", adresse, valeur)]);
        Assert.NotEqual(SessionSpeedrun.Finished, c.Etat);
    }

    [Fact]
    public void Six_boss_six_etats_distincts_un_meme_MEM_que_le_couteau()
    {
        var couteau = Lire("metal-slug-3.mission-1-knife.v7.json");
        var boss = Boss().Select(b => Lire((string)b[0])).ToList();
        Assert.Equal(6, boss.Select(b => b.Etat!.Sha256).Distinct().Count());
        Assert.All(boss, b => Assert.Equal(couteau.MemSha256, b.MemSha256));
        Assert.Equal([1, 2, 3, 4, 5, 6], boss.Select(b => b.Ordre));
        foreach (var langue in new[] { "fr", "en", "es", "ja", "ko", "zh" })
            Assert.All(boss, b => Assert.False(string.IsNullOrWhiteSpace(b.Resume(langue))));
    }
}
