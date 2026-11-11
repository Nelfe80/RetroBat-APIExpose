using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// OU LIRE UNE ADRESSE DU .MEM PAR READ_CORE_RAM (11/11) : la traduction du wrapper pour FBNeo (table des banques, repli des
/// RAM de travail CPS et Neo-Geo en mots inverses), les index bruts tels quels. Mesure : Metal Slug 3 du 12/10, score lu a
/// 0xED18 en mots inverses, 33 220 (1LC certifie) relu dans le replay.
/// </summary>
public class AdressesDuCoeurTests
{
    private static readonly IReadOnlyList<AdressesDuCoeur.Banque> BubbleBobble =
        [new(0x0200, 0x1D00, 0xC000, false), new(0x3300, 0x1800, 0xE000, false)];

    [Fact]
    public void Neo_Geo_et_CPS_se_replient_en_mots_inverses()
    {
        Assert.Equal([0xED19L, 0xED18L, 0xED1BL, 0xED1AL], AdressesDuCoeur.Places("fbneo", 0x10ED18, 4, null));
        Assert.Equal([0x8307L, 0x8306L], AdressesDuCoeur.Places("fbneo", 0xFF8306, 2, null));
        Assert.Equal([0x4D14L], AdressesDuCoeur.Places("fbneo", 0xFF4D15, 1, null));
    }

    [Fact]
    public void La_table_du_jeu_passe_avant_le_repli()
    {
        Assert.Equal([0x3941L, 0x3942L, 0x3943L], AdressesDuCoeur.Places("fbneo", 0xE641, 3, BubbleBobble));
        var mots = new List<AdressesDuCoeur.Banque> { new(0, 0x4000, 0xFFC000, true) };
        Assert.Equal([0x0011L, 0x0010L], AdressesDuCoeur.Places("fbneo", 0xFFC010, 2, mots));
    }

    [Fact]
    public void Un_index_brut_reste_tel_quel_et_l_inconnu_ne_se_lit_pas()
    {
        Assert.Equal([0x166CL, 0x166DL, 0x166EL, 0x166FL], AdressesDuCoeur.Places("fbneo", 0x166C, 4, null));
        Assert.Null(AdressesDuCoeur.Places("fbneo", 0x200000, 2, null));
        Assert.Equal([0xEF96L], AdressesDuCoeur.Places("genesis_plus_gx", 0xEF96, 1, null));
    }

    [Fact]
    public void Le_score_de_Metal_Slug_3_se_lit_sous_FBNeo()
    {
        const string mem = """
return {
  events = {
    scoring = {
      points = {
        { address=0X10ED18, type="u32be", condition="change", action="SCORE_STATE", score_kind="game", score_encoding="bcd", desc="Player 1 score BCD" },
        { address=0X10ED20, type="u32be", condition="change", action="SCORE_STATE", score_kind="game", score_encoding="bcd", player=2, desc="Player 2 score BCD" },
      },
    },
  },
}
""";
        using var lecteur = LecteurDuScore.Depuis("arcade", "metal-slug-3", "metal-slug-3.MEM", mem, "fbneo");
        Assert.NotNull(lecteur);
        var ligne = Assert.Single(lecteur!.Lignes);
        Assert.Equal([0xED19L, 0xED18L, 0xED1BL, 0xED1AL], ligne.Index);
        // Le bloc brut de FBNeo a 0xED18 : 03 00 20 32 ; remis dans l'ordre de l'adresse MAME : 00 03 32 20 = 33 220 en BCD.
        var brut = new Dictionary<long, int> { [0xED18] = 0x03, [0xED19] = 0x00, [0xED1A] = 0x20, [0xED1B] = 0x32 };
        var valeur = LecteurDuScore.Valeur(ligne, ligne.Index.Select(i => brut[i]).ToList());
        Assert.Equal(33220, lecteur.Composer([valeur]));
    }
}
