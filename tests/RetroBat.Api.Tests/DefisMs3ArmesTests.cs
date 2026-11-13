using System.Globalization;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// METAL SLUG 3, RAMBO ET PISTOLET PUR (challenges choisis par le user le 13/11). Meme depart et meme arrivee que le couteau v7
/// (etat au parachute, chrono au poser du soldat, fin de la mission 1), meme .MEM de la couche des challenges ; seuls les
/// verrous changent. Mesures du banc sans fenetre du 13/11 (coeur FBNeo de la borne, replays de la borne) :
/// - l'arme est le mot 0x10EAF2 (FF00 pistolet, FF05 Heavy Machine Gun), les munitions le mot 0x10EAFA (FFFF = infini au
///   pistolet, 03E7 = 999) ; une caisse ecrit la nouvelle arme pendant UNE image, le verrou la remet a l'image suivante ;
/// - le dessin de l'arme suit le bit 0 du corps 0x10058F (et du corps d'avant 0x10058E) : 00 pistolet, 01 arme lourde (table
///   0x021B3E du programme) ; sans ce verrou, le soldat garde le dessin de la HMG ramassee en tirant au pistolet ;
/// - Rambo tient la jauge de nourriture 0x10EAF6 a 0 : gros (02) n'a pas de variante, le bit 0 en ferait une momie (03).
/// Morts permises dans la limite des vies ; un continue annule (comme le couteau).
/// </summary>
public class DefisMs3ArmesTests
{
    private static string Fichier(string nom) => Path.Combine(AppContext.BaseDirectory, "Reglements", nom);

    private static Reglement Lire(string nom) => Reglement.Lire(File.ReadAllText(Fichier(nom)));

    private static readonly Reglement Couteau = Lire("metal-slug-3.mission-1-knife.v7.json");

    private static int _ids;

    private static Signal S(long frame, string action, string adresse, int valeur)
        => Signal.Depuis(frame, action, adresse, valeur, 1_000_000 + (long)(frame * 1000 / 59.185606));

    /// <summary>Le depart au parachute, le soldat pose, un passage en Metal Slug, la fin de la mission 1 ; une mort en route, un continue.</summary>
    private static string Jouer(Reglement r, bool mort = false, bool continuer = false)
    {
        var s = new SessionSpeedrun(r.ChallengeId, r, () => "a" + Interlocked.Increment(ref _ids).ToString(CultureInfo.InvariantCulture));
        s.RecevoirImage([S(800, "GAME_START", "0x10FDB6", 0x01)]);
        s.RecevoirImage([S(803, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
        s.RecevoirImage([S(900, "PROGRESSION_STAGE", "0x10040C", 0x00), S(900, "PROGRESSION_STAGE", "0x10040D", 0x00)]);
        foreach (var a in s.AdressesInconnues) s.Relever(a, a == 0x10FDB6 ? 1 : 0);
        s.RecevoirImage([S(1005, "MOUNT_STATE", "0x1002C1", 0x01)]);
        if (mort) s.RecevoirImage([S(3729, "LOSE_LIFE", "0x1002BB", 0x01)]);
        if (continuer) s.RecevoirImage([S(5000, "CONTINUE_SCREEN", "0x10FDB6", 0x02)]);
        s.RecevoirImage([S(9800, "MOUNT_STATE", "0x1002C1", 0x04)]);
        s.RecevoirImage([S(11000, "PROGRESSION_STAGE", "0x10040D", 0x03)]);
        s.RecevoirImage([S(12500, "MOUNT_STATE", "0x1002C1", 0x01)]);
        s.RecevoirImage([S(13000, "PROGRESSION_STAGE", "0x10040D", 0x04)]);
        s.RecevoirImage([S(16000, "PROGRESSION_STAGE", "0x10040C", 0x08)]);
        return s.Etat;
    }

    [Theory]
    [InlineData("metal-slug-3.mission-1-rambo.json", "ch-ms3-m1-rambo-v1", 1)]
    [InlineData("metal-slug-3.mission-1-pistol.json", "ch-ms3-m1-pistol-v1", 2)]
    public void Meme_depart_meme_arrivee_meme_MEM_que_le_couteau(string fichier, string ruleset, int ordre)
    {
        var r = Lire(fichier);
        Assert.Equal((ruleset, 1, ordre, "challenge_rule"), (r.Ruleset, r.Version, r.Ordre, r.Kind));
        Assert.True(r.EstUnChallenge);
        Assert.Equal(Couteau.MemSha256, r.MemSha256);
        Assert.Equal(Couteau.Etat!.Sha256, r.Etat!.Sha256);
        Assert.Empty(r.VerifierContre(File.ReadAllBytes(Fichier("metal-slug-3.v5.MEM"))));
        Assert.Empty(r.Compteurs);
        Assert.Empty(r.Echecs);
        foreach (var langue in new[] { "fr", "en", "es", "ja", "ko", "zh" })
        {
            Assert.NotEqual(r.ChallengeId, r.Titre(langue));
            Assert.False(string.IsNullOrWhiteSpace(r.Resume(langue)));
        }
    }

    [Fact]
    public void Rambo_tient_la_HMG_ses_999_munitions_son_dessin_et_la_jauge_de_nourriture()
        => Assert.Equal("0x10EAF2,1,value,0xFF;0x10EAF3,1,value,0x05;0x10EAFA,1,value,0x03;0x10EAFB,1,value,0xE7;"
                        + "0x10058F,1,set_bits,0x01;0x10058E,1,set_bits,0x01;0x10EAF6,1,value,0x00;0x10EAF7,1,value,0x00",
            Lire("metal-slug-3.mission-1-rambo.json").LignesDesVerrous);

    [Fact]
    public void Pistolet_pur_tient_le_pistolet_l_infini_et_le_dessin_du_pistolet()
        => Assert.Equal("0x10EAF2,1,value,0xFF;0x10EAF3,1,value,0x00;0x10EAFA,1,value,0xFF;0x10EAFB,1,value,0xFF;"
                        + "0x10058F,1,clear_bits,0x01;0x10058E,1,clear_bits,0x01",
            Lire("metal-slug-3.mission-1-pistol.json").LignesDesVerrous);

    [Theory]
    [InlineData("metal-slug-3.mission-1-rambo.json")]
    [InlineData("metal-slug-3.mission-1-pistol.json")]
    public void La_mission_1_finie_est_classee_meme_avec_une_mort(string fichier)
    {
        Assert.Equal("FINISHED", Jouer(Lire(fichier)));
        Assert.Equal("FINISHED", Jouer(Lire(fichier), mort: true));
    }

    [Theory]
    [InlineData("metal-slug-3.mission-1-rambo.json")]
    [InlineData("metal-slug-3.mission-1-pistol.json")]
    public void Un_continue_annule(string fichier)
        => Assert.NotEqual("FINISHED", Jouer(Lire(fichier), continuer: true));
}
