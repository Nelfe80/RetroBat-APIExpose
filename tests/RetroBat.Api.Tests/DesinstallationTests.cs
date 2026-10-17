using System.Xml.Linq;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La desinstallation propre (2026-10-17) : tout part, sauf les medias, l'identite de la borne et les replays ; seuls les
/// reglages d'APIExpose sortent d'es_settings.cfg.
/// </summary>
public sealed class DesinstallationTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "apiexpose-desinstallation-" + Guid.NewGuid().ToString("N"));

    public DesinstallationTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, true); } catch (IOException) { }
    }

    private void Poser(string relatif)
    {
        var chemin = Path.Combine(_racine, relatif.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, "x");
    }

    [Fact]
    public void Tout_part_sauf_les_medias_l_identite_de_la_borne_et_les_replays()
    {
        foreach (var f in new[]
                 {
                     "media/fbneo/images/1942.png", "state/nelfeplay/device.json", "state/nelfeplay/certified/v.json",
                     "state/nelfeplay/collection-nelfeplay-scoring.json", "state/nelfenet/replay/manifests/rp_1.json",
                     "state/nelfenet/replay/objects/sha256/ab/ab.replay.gz", "state/nelfenet/peers.json", "resources/ram/arcade/1942.MEM",
                     ".log/apiexpose-runtime.log", "appsettings.json", "wrapper/wrapper.dll", "wrapper/.env", "RetroBat.Api.exe",
                     "RetroBat.Api.Update.exe", "unins000.exe", "unins000.dat", "media-index.json",
                 })
        {
            Poser(f);
        }

        var aSupprimer = Desinstallation.ASupprimer(_racine, Desinstallation.Gardes, "RetroBat.Api.exe").OrderBy(f => f).ToList();

        Assert.Equal(new[]
        {
            ".log/apiexpose-runtime.log", "appsettings.json", "media-index.json", "resources/ram/arcade/1942.MEM",
            "RetroBat.Api.Update.exe", "state/nelfenet/peers.json", "state/nelfeplay/certified/v.json",
            "state/nelfeplay/collection-nelfeplay-scoring.json", "wrapper/.env", "wrapper/wrapper.dll",
        }.OrderBy(f => f), aSupprimer);
    }

    [Theory]
    [InlineData(@"E:\RetroBat\plugins\APIExpose", true)]
    [InlineData(@"D:\Jeux\RetroBat\plugins\apiexpose\", true)]
    [InlineData(@"E:\RetroBat", false)]                  // une racine mal trouvee : jamais RetroBat entier
    [InlineData(@"E:\RetroBat\plugins", false)]
    [InlineData(@"C:\Users\joueur\Downloads\APIExpose", false)]
    public void Le_nettoyage_ne_tourne_que_dans_plugins_APIExpose(string dossier, bool attendu)
        => Assert.Equal(attendu, Desinstallation.DossierDuPlugin(dossier));

    [Fact]
    public void Seuls_les_reglages_d_APIExpose_sortent_d_es_settings()
    {
        var document = XDocument.Parse("""
            <config>
              <string name="global.apiexpose.scraping.auto_enabled" value="0" />
              <bool name="global.apiexpose.local_media_manager.enabled" value="true" />
              <string name="CollectionSystemsCustom" value="mario,nelfeplay-scoring" />
              <string name="global.retroachievements" value="1" />
            </config>
            """);

        Assert.True(Desinstallation.RetirerLesReglages(document));
        var restants = document.Descendants().Select(e => (string?)e.Attribute("name")).Where(n => n is not null).ToList();
        Assert.Equal(new[] { "CollectionSystemsCustom", "global.retroachievements" }, restants);
        Assert.False(Desinstallation.RetirerLesReglages(document));
    }
}
