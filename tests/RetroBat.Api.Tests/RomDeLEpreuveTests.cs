using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA ROM D'UNE EPREUVE LANCEE PAR LA ROUTE (12/11). Essai reel de la nuit : la route a lance « Super Mario Bros (E).nes », le
/// premier fichier du gamelist du groupe, que le coeur hote des fantomes refuse ; World Scoring et le panneau retiennent
/// « Super Mario Bros. (World).zip », la ROM de reference des speedruns.
/// </summary>
public sealed class RomDeLEpreuveTests
{
    private const string Pal = @"E:\RetroBat\roms\nes\Super Mario Bros (E).nes";
    private const string World = "E:/RetroBat/roms/nes/Super Mario Bros. (World).zip";

    private static readonly Dictionary<string, string> Passe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nes/super-mario-bros"] = World,
        ["arcade/metal-slug-3"] = @"E:\RetroBat\roms\fbneo\mslug3.zip",
    };

    [Fact]
    public void La_route_prend_la_ROM_de_reference_retenue_par_World_Scoring_jamais_le_premier_dump_du_gamelist()
    {
        var r = RomDeLEpreuve.Choisir(Passe, "nes", "super-mario-bros", () => true, () => Pal, _ => true);
        Assert.Equal(World, r);
    }

    [Fact]
    public void Un_jeu_a_speedruns_ecarte_par_World_Scoring_n_a_pas_d_epreuve_comme_au_panneau()
    {
        // ROM de reference absente de la borne : World Scoring ne l'a pas mis dans les defis, la route ne lance pas un autre dump
        var r = RomDeLEpreuve.Choisir(new Dictionary<string, string>(), "nes", "super-mario-bros", () => true, () => Pal, _ => true);
        Assert.Null(r);
    }

    [Fact]
    public void Le_fichier_retenu_disparu_n_est_pas_remplace_par_un_autre_dump()
    {
        var r = RomDeLEpreuve.Choisir(Passe, "nes", "super-mario-bros", () => true, () => Pal, f => f != World);
        Assert.Null(r);
    }

    [Fact]
    public void Avant_la_premiere_passe_un_jeu_a_reference_attend_et_un_challenge_d_arcade_garde_le_repli()
    {
        Assert.Null(RomDeLEpreuve.Choisir(null, "nes", "super-mario-bros", () => true, () => Pal, _ => true));
        Assert.Equal("mslug3", RomDeLEpreuve.Choisir(null, "arcade", "metal-slug-3", () => false, () => "mslug3", _ => true));
    }

    [Fact]
    public void Un_challenge_d_arcade_prend_le_fichier_de_World_Scoring_et_garde_le_repli_s_il_n_y_est_pas()
    {
        Assert.Equal(@"E:\RetroBat\roms\fbneo\mslug3.zip", RomDeLEpreuve.Choisir(Passe, "arcade", "metal-slug-3", () => false, () => "autre", _ => true));
        Assert.Equal("autre", RomDeLEpreuve.Choisir(Passe, "arcade", "ketsui", () => false, () => "autre", _ => true));
    }
}
