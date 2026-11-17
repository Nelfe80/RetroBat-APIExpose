using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Support kexian7740 (17/11) : un jeu de roms\mame est « arcade » pour l'API ; ses medias de gamelist se lisent dans
/// roms\mame, jamais dans roms\arcade. Le dossier vient du chemin de la ROM, puis du systeme d'ES, puis du canonique.
/// </summary>
public sealed class DossiersDeLaGamelistTests
{
    private const string Roms = @"C:\RetroBat\roms";

    [Fact]
    public void Un_jeu_mame_cherche_d_abord_dans_roms_mame()
    {
        var dossiers = PhysicalMediaWebSocketProjectionService.DossiersDeLaGamelist(
            @"C:\RetroBat\roms\mame\mslug.zip", "mame", "arcade", Roms);
        Assert.Equal(new[] { "mame", "arcade" }, dossiers);
    }

    [Fact]
    public void Depuis_une_collection_le_dossier_vient_du_chemin()
    {
        var dossiers = PhysicalMediaWebSocketProjectionService.DossiersDeLaGamelist(
            @"C:\RetroBat\roms\fbneo\sf2.zip", "nelfeplay-scoring", "arcade", Roms);
        Assert.Equal(new[] { "fbneo", "nelfeplay-scoring", "arcade" }, dossiers);
    }

    [Fact]
    public void Un_chemin_hors_de_roms_ou_relatif_ne_donne_que_les_systemes()
    {
        Assert.Equal(new[] { "mame", "arcade" },
            PhysicalMediaWebSocketProjectionService.DossiersDeLaGamelist(@"D:\Ailleurs\mslug.zip", "mame", "arcade", Roms));
        Assert.Equal(new[] { "mame", "arcade" },
            PhysicalMediaWebSocketProjectionService.DossiersDeLaGamelist("./mslug.zip", "mame", "arcade", Roms));
    }

    [Fact]
    public void Un_systeme_deja_present_n_est_pas_repete()
    {
        var dossiers = PhysicalMediaWebSocketProjectionService.DossiersDeLaGamelist(
            @"C:\RetroBat\roms\naomi2\vf4evoct.zip", "naomi2", "naomi2", Roms);
        Assert.Equal(new[] { "naomi2" }, dossiers);
    }
}
