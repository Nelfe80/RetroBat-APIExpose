using System.Xml.Linq;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Option « FLASH AU RECORD BATTU » (17/11) : eteinte par defaut ; allumee, le flash rapide de RetroArch (1) dit au joueur
/// que le record tombe, tout autre message de RetroArch reste coupe ; « TOUT MASQUER » l'emporte.
/// </summary>
public sealed class FlashDuRecordTests
{
    private static XDocument Es(params (string Cle, string Valeur)[] reglages)
        => new(new XElement("config", reglages.Select(r =>
            new XElement("string", new XAttribute("name", r.Cle), new XAttribute("value", r.Valeur)))));

    [Fact]
    public void Eteinte_par_defaut_le_flash_reste_coupe()
    {
        Assert.False(NotificationsWorldScoringPreference.FlashDuRecord(Es()));
        Assert.False(NotificationsWorldScoringPreference.FlashDuRecord(Es((NotificationsWorldScoringPreference.FlashKey, ""))));
        Assert.Equal("2", NotificationsRetroArch.AutresNotifications.Single(r => r.Key == NotificationsRetroArch.CleDuFlash).Value);
    }

    [Fact]
    public void Allumee_seul_le_flash_change_et_passe_au_flash_rapide()
    {
        Assert.True(NotificationsWorldScoringPreference.FlashDuRecord(Es((NotificationsWorldScoringPreference.FlashKey, "1"))));
        var avec = NotificationsRetroArch.AutresNotificationsPour(true);
        Assert.Equal("1", avec.Single(r => r.Key == NotificationsRetroArch.CleDuFlash).Value);
        Assert.All(avec.Where(r => r.Key != NotificationsRetroArch.CleDuFlash), r => Assert.Equal("false", r.Value));
        Assert.Contains("-retroarch.notification_show_screenshot_flash 1", string.Join(' ', NotificationsRetroArch.AjouterSaufSucces([], flash: true)));
    }

    [Fact]
    public void Sans_RetroAchievements_le_flash_garde_les_widgets_sauf_si_tout_est_masque()
    {
        var flash = Es((NotificationsWorldScoringPreference.FlashKey, "1"));
        Assert.True(NotificationsRetroArch.GarderLesSucces(flash, "fbneo", "sf2.zip"));
        Assert.False(NotificationsRetroArch.GarderLesSucces(Es(), "fbneo", "sf2.zip"));
        var toutMasque = Es((NotificationsWorldScoringPreference.FlashKey, "1"), (NotificationsWorldScoringPreference.EsKey, "none"));
        Assert.False(NotificationsRetroArch.GarderLesSucces(toutMasque, "fbneo", "sf2.zip"));
    }

    [Fact]
    public void La_cle_reste_une_cle_apiexpose()
        => Assert.StartsWith("global.apiexpose.", NotificationsWorldScoringPreference.FlashKey);
}
