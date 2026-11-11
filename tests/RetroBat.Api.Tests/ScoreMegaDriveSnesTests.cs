using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>LE SCORE LU SOUS GENESIS PLUS GX ET SNES9X (11/11), controle de fidelite des films Mega Drive et SNES.</summary>
public sealed class ScoreMegaDriveSnesTests
{
    /// <summary>
    /// LE SCORE MEGA DRIVE COMPOSE PAR LE LECTEUR (11/11) : Streets of Rage 2, trois octets BCD lus par READ_CORE_RAM a
    /// 0xEF96/97/99 (repere de Genesis Plus GX, sans ^1). Sur la borne, en fin du replay rp_06GVYGMP (recalage) : 39, 00, 60.
    /// </summary>
    [Fact]
    public void Le_score_de_Streets_of_Rage_2_se_compose_depuis_ses_octets_bcd()
    {
        const string mem = """
return {
  events = {
    scoring = {
      points = {
        { address=0XEF96, type="u8", condition="change", action="SCORE_STATE", player=1, score_kind="game", score_mask="00XX00", score_encoding="bcd", desc="[8-bit BCD] Player 1 Score: 00XX00" },
        { address=0XEF97, type="u8", condition="change", action="SCORE_STATE", player=1, score_kind="game", score_mask="XX0000", score_encoding="bcd", desc="[8-bit BCD] Player 1 Score: XX0000" },
        { address=0XEF99, type="u8", condition="change", action="SCORE_STATE", player=1, score_kind="game", score_mask="0000XX", score_encoding="bcd", desc="[8-bit BCD] Player 1 Score: 0000XX" },
        { address=0XF096, type="u8", condition="change", action="SCORE_STATE", player=2, score_kind="game", score_mask="00XX00", score_encoding="bcd", desc="[8-bit BCD] Player 2 Score: 00XX00" },
      },
    },
  },
}
""";
        using var lecteur = LecteurDuScore.Depuis("megadrive", "streets-of-rage-2", "streets-of-rage-2.MEM", mem);
        Assert.NotNull(lecteur);
        Assert.Equal([0xEF96L, 0xEF97L, 0xEF99L], lecteur!.Lignes.Select(l => l.Adresse));
        var octets = new Dictionary<long, int> { [0xEF96] = 0x39, [0xEF97] = 0x00, [0xEF99] = 0x60 };
        Assert.Equal(3960, lecteur.Composer(lecteur.Lignes.Select(l => (long?)octets[l.Adresse]).ToList()));
        // Le score de neogeoff (rp_06GW30HW, rejoue sans recalage jusqu'a la fin) : 10 75 80.
        var neogeoff = new Dictionary<long, int> { [0xEF96] = 0x75, [0xEF97] = 0x10, [0xEF99] = 0x80 };
        Assert.Equal(107580, lecteur.Composer(lecteur.Lignes.Select(l => (long?)neogeoff[l.Adresse]).ToList()));
    }
}
