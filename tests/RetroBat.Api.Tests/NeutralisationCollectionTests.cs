using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les jeux de la collection World Scoring recoivent leurs cles par jeu (rembobinage, run-ahead,
/// sauvegarde auto) des le demarrage de l'API, avant l'ouverture d'EmulationStation. Un testeur a
/// lance Sonic 18 s apres le demarrage de son API, sans selection prealable : le rembobinage etait
/// actif et 56 minutes de jeu ont ete refusees (2026-09-25).
/// </summary>
public class NeutralisationCollectionTests
{
    [Fact]
    public void Chaque_ligne_donne_son_systeme_et_son_fichier()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "nelfe-coll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var roms = Path.Combine(dossier, "roms");
            var home = Path.Combine(dossier, "emulationstation");
            var collection = Path.Combine(dossier, "custom-nelfeplay-scoring.cfg");
            File.WriteAllLines(collection,
            [
                // La collection de la borne de test, telle qu'ecrite le 2026-09-25.
                Path.Combine(roms, "fbneo", "19xx.zip").Replace('\\', '/'),
                Path.Combine(roms, "megadrive", "Sonic The Hedgehog (USA, Europe).zip").Replace('\\', '/'),
                "~/../roms/mame/altbeast.zip",
                "",
                // Hors de roms/ : ignore, on ne pose pas de cle pour un chemin qu'ES ne lancerait pas.
                Path.Combine(dossier, "ailleurs", "jeu.zip"),
            ]);

            var jeux = CertifiedSettingsService.JeuxDeLaCollection(collection, roms, home);

            Assert.Equal(new[]
            {
                ("fbneo", "19xx.zip"),
                ("megadrive", "Sonic The Hedgehog (USA, Europe).zip"),
                ("mame", "altbeast.zip"),
            }, jeux.ToArray());
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    // Au lancement (2026-10-18, Turbo) : le jeu lance par le crochet game-start se retrouve dans la collection.
    [Theory]
    [InlineData("mame", "mspacmab.zip", true)]
    [InlineData("MAME", "MSPACMAB.ZIP", true)]
    [InlineData("fbneo", "mspacmab.zip", false)]
    [InlineData("mame", "pacman.zip", false)]
    public void Le_jeu_lance_se_retrouve_dans_la_collection(string systeme, string fichier, bool attendu)
        => Assert.Equal(attendu, CertifiedSettingsService.DansLaCollection(
            new[] { ("mame", "mspacmab.zip"), ("megadrive", "Sonic The Hedgehog (Japan, Korea).md") }, systeme, fichier));

    [Fact]
    public void Le_crochet_game_start_donne_le_systeme_et_la_rom()
    {
        var (systeme, chemin) = RetroBat.Api.Replay.Video.EchelleDesFilms.LancementDuScript(
            "\"G:\\Emulation\\Retrobat\\roms\\mame\\mspacmab.zip\" \"Ms. Pac-Man\" \"mspacmab\"");
        Assert.Equal("mame", systeme);
        Assert.Equal("mspacmab.zip", Path.GetFileName(chemin));
    }

    [Fact]
    public void Sans_collection_aucun_jeu()
    {
        Assert.Empty(CertifiedSettingsService.JeuxDeLaCollection(
            Path.Combine(Path.GetTempPath(), "absente-" + Guid.NewGuid().ToString("N") + ".cfg"), @"E:\RetroBat\roms", @"E:\RetroBat\emulationstation"));
    }
}
