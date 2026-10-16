using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// TGM2 (Psikyo SH-2, 2026-10-16) : FBNeo range la RAM du SH-2 en mots de 32 bits petit-boutistes. Les adresses du
/// .MEM (offsets du blob du wrapper, ecrits pour FBNeo) se traduisent pour MAME avec l'octet et l'ordre du processeur.
/// Les valeurs attendues ont ete mesurees sous MAME 0.289 (sonde sans fenetre).
/// </summary>
public class MameBanquesSwap32Tests
{
    private static readonly List<(long From, long Size, long Base, bool Swap32)> Tgm2 = [(0x30200, 0x100000, 0x6000000, true)];

    [Fact]
    public void Les_valeurs_de_tgm2_se_lisent_a_l_adresse_du_processeur()
    {
        // Le score (u24le chez FBNeo) : les trois octets bas du mot gros-boutiste de 0x6064BC8.
        Assert.Equal((0x6064BC9L, "u24be"), MameLuaIngameProvider.ApplyBanks(Tgm2, 0x94DC8, "u24le"));
        // Le niveau (u16le) : la moitie basse du mot de 0x6064BB8.
        Assert.Equal((0x6064BBAL, "u16be"), MameLuaIngameProvider.ApplyBanks(Tgm2, 0x94DB8, "u16le"));
        // Un octet change de place dans son mot : le mode, les credits.
        Assert.Equal((0x6064BA5L, "u8"), MameLuaIngameProvider.ApplyBanks(Tgm2, 0x94DA6, "u8"));
        Assert.Equal((0x6064762L, "u8"), MameLuaIngameProvider.ApplyBanks(Tgm2, 0x94961, "u8"));
        // Un mot entier.
        Assert.Equal((0x6064BE8L, "u32be"), MameLuaIngameProvider.ApplyBanks(Tgm2, 0x94DE8, "u32le"));
    }

    [Fact]
    public void Une_banque_sans_inversion_ne_change_pas()
    {
        // Shinobi (68000, mots de 16 bits) : la table translate, le type reste celui du .MEM.
        var shinobi = new List<(long From, long Size, long Base, bool Swap32)> { (0x0000, 0x4000, 0xFFC000, false) };
        Assert.Equal((0xFFD751L, "u8"), MameLuaIngameProvider.ApplyBanks(shinobi, 0x1751, "u8"));
        // Hors de toute banque : l'adresse du .MEM telle quelle.
        Assert.Equal((0xFFD740L, "u32be"), MameLuaIngameProvider.ApplyBanks(shinobi, 0xFFD740, "u32be"));
    }
}
