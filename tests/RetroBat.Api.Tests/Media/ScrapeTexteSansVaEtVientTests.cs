using System;
using System.IO;
using System.Text.Json;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// 2026-09-26, 19xx : chaque visite d'une fiche redemandait le jeu a ScreenScraper. ScreenScraper
/// ne connait pas la langue de la ROM et renvoyait une cle « lang » vide, qui effacait la langue de
/// la gamelist ; la selection suivante la remettait. Ce va-et-vient passait pour une mise a jour du
/// texte, donc jamais pour « rien de neuf », et la memoire de la file s'effacait au redemarrage.
/// </summary>
public class ScrapeTexteSansVaEtVientTests
{
    private static JsonElement Jeu(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Une_langue_de_rom_inconnue_n_efface_pas_celle_de_la_gamelist()
    {
        var champs = ScreenScraperRawCacheMetadataService.BuildFields(
            Jeu("""{ "id": "37386", "noms": [ { "region": "wor", "text": "19xx" } ] }"""), "fbneo", "en");

        Assert.False(champs.ContainsKey("lang"));
    }

    [Fact]
    public void Une_langue_de_rom_connue_est_transmise()
    {
        var champs = ScreenScraperRawCacheMetadataService.BuildFields(
            Jeu("""{ "id": "1", "noms": [ { "region": "wor", "text": "Jeu" } ], "langues": "fr" }"""), "megadrive", "fr");

        Assert.True(champs.TryGetValue("lang", out var langue));
        Assert.False(string.IsNullOrWhiteSpace(langue));
    }

    [Fact]
    public void Rien_de_neuf_survit_au_redemarrage()
    {
        var fichier = Path.Combine(Path.GetTempPath(), "nochange-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var maintenant = DateTime.UtcNow;
            new RemoteScrapeNoChangeCooldowns(fichier).Remember("fbneo|19xx.zip|cartridge,label", maintenant.AddHours(12));

            var apresRedemarrage = new RemoteScrapeNoChangeCooldowns(fichier);
            Assert.True(apresRedemarrage.IsActive("FBNEO|19xx.zip|cartridge,label", maintenant));
            Assert.False(apresRedemarrage.IsActive("fbneo|1942.zip|flyer", maintenant));
        }
        finally
        {
            File.Delete(fichier);
        }
    }

    [Fact]
    public void Une_echeance_passee_ne_revient_pas()
    {
        var fichier = Path.Combine(Path.GetTempPath(), "nochange-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(fichier, JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, DateTime>
            {
                ["vieux"] = DateTime.UtcNow.AddHours(-1),
                ["frais"] = DateTime.UtcNow.AddHours(1),
            }));

            var memoire = new RemoteScrapeNoChangeCooldowns(fichier);
            Assert.Equal(1, memoire.Count);
            Assert.False(memoire.IsActive("vieux", DateTime.UtcNow));
        }
        finally
        {
            File.Delete(fichier);
        }
    }

    [Fact]
    public void Un_fichier_illisible_repart_a_vide()
    {
        var fichier = Path.Combine(Path.GetTempPath(), "nochange-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(fichier, "{ pas du json");
            var memoire = new RemoteScrapeNoChangeCooldowns(fichier);
            Assert.False(memoire.IsActive("fbneo|19xx.zip|cartridge,label", DateTime.UtcNow));
            memoire.Remember("k", DateTime.UtcNow.AddHours(1));
            Assert.True(new RemoteScrapeNoChangeCooldowns(fichier).IsActive("k", DateTime.UtcNow));
        }
        finally
        {
            File.Delete(fichier);
        }
    }
}
