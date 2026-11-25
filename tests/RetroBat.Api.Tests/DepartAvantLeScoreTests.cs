using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 25/11, Blazing Star sous FBNeo : le test memoire du BIOS Neo Geo ecrivait 0x5555 0x5555 dans le mot du score, avant toute
/// partie. 55 555 555 « battait » le record du monde (rang du defi, capture, direct, plaque du Deck). Un score n'est celui du
/// joueur qu'apres le depart de la partie.
/// </summary>
public sealed class DepartAvantLeScoreTests
{
    [Fact]
    public void Avant_tout_depart_avec_une_manette_lue_le_score_est_hors_partie()
    {
        Assert.False(NelfePlayScoringReporter.DepartVu(startVu: false, gameStartVu: false, creditOuDepart: false, etatDeDepart: false,
            appuiDepuisLeChargement: false, voitLesAppuis: true));
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void Chaque_depart_ouvre_la_partie(bool start, bool gameStart, bool credit, bool etat, bool appui)
    {
        Assert.True(NelfePlayScoringReporter.DepartVu(start, gameStart, credit, etat, appui, voitLesAppuis: true));
    }

    [Fact]
    public void Une_borne_qui_ne_voit_aucun_appui_garde_le_comportement_d_avant()
    {
        Assert.True(NelfePlayScoringReporter.DepartVu(false, false, false, false, false, voitLesAppuis: false));
    }

    [Fact]
    public void Le_rapporteur_neuf_dit_hors_partie_tant_que_le_depart_n_est_pas_vu()
    {
        PanneauDeLaBorne.Appareils = new[] { new PlayerNumbering.Device("030000005e040000fd02000003097200", "Xbox One S Controller") };
        try
        {
            var rapporteur = new NelfePlayScoringReporter(new SimpleEventBus(), new HttpClientFactoryBidon(), new NelfePlayDeviceStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<NelfePlayDeviceStore>.Instance));
            Assert.True(rapporteur.ScoreHorsPartie());
        }
        finally
        {
            PanneauDeLaBorne.Appareils = null;
        }
    }

    [Fact]
    public void Le_constat_du_decor_dit_ce_que_retroarch_a_recu()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "constat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var png = Path.Combine(dossier, "decorations", "nelfeplay-deck", "games", "neogeo", "blazstar.png");
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            File.WriteAllBytes(png, [1]);
            var cfg = Path.Combine(dossier, "retroarch.cfg");
            File.WriteAllText(cfg, "input_overlay = \":/custom-overlay.cfg\"\ninput_overlay_enable = \"true\"\naspect_ratio_index = \"23\"\ncustom_viewport_x = \"480\"\ncustom_viewport_y = \"0\"\ncustom_viewport_width = \"1440\"\ncustom_viewport_height = \"1080\"\n");
            var ov = Path.Combine(dossier, "custom-overlay.cfg");
            File.WriteAllText(ov, "overlay0_overlay = \"" + png + "\"\n");
            var texte = DeckDecor.Constat(cfg, ov);
            Assert.Contains("du Deck", texte);
            Assert.Contains("presente", texte);
            Assert.Contains("480,0 1440x1080", texte);
            File.Delete(png);
            Assert.Contains("ABSENTE", DeckDecor.Constat(cfg, ov));
            Assert.Contains("aucune", DeckDecor.Constat(cfg, Path.Combine(dossier, "rien.cfg")));
        }
        finally
        {
            Directory.Delete(dossier, true);
        }
    }

    private sealed class HttpClientFactoryBidon : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
