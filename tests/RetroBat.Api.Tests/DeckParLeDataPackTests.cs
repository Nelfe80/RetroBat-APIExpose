using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES PLAYER DECKS PAR LE DATA PACK (decision du user du 21/11) : le dossier <c>playerdeck</c> du Data Pack n'admet que les
/// themes et les releves de niveaux, et le Deck (comme le catalogue de l'API) lit ces themes AVANT ceux de tools\playerdeck.
/// </summary>
public sealed class DeckParLeDataPackTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "deck-datapack-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_racine, true); } catch (IOException) { }
    }

    private static readonly HashSet<string> Dossiers = new(DataPackOptions_Dossiers(), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> DataPackOptions_Dossiers() => new DataPackOptions().Folders;

    [Fact]
    public void Le_Data_Pack_suit_le_dossier_playerdeck()
        => Assert.Contains("playerdeck", Dossiers);

    [Theory]
    [InlineData("playerdeck/themes/x.json", true)]
    [InlineData("playerdeck/themes/19xx-hangar.json", true)]
    [InlineData("playerdeck/themes/atlas/d/stage1.json", true)]
    [InlineData("playerdeck/themes/atlas/dodonpachi-daioujou-brass/stage1-p2.json", true)]
    [InlineData("playerdeck/themes/atlas/d/stage1_bg.png", true)]      // le fond facultatif d'un niveau
    [InlineData("playerdeck/NelfePlay.PlayerDeck.exe", false)]          // jamais un exe
    [InlineData("playerdeck/themes/x.exe", false)]
    [InlineData("playerdeck/themes/x.dll", false)]
    [InlineData("playerdeck/themes/x.png", false)]                      // un PNG ailleurs que dans atlas
    [InlineData("playerdeck/themes/x.JSON.ps1", false)]
    [InlineData("playerdeck/themes/sous/x.json", false)]                // un sous-dossier autre qu'atlas
    [InlineData("playerdeck/themes/atlas/x.json", false)]               // un releve sans dossier de Deck
    [InlineData("playerdeck/themes/atlas/d/e/stage1.json", false)]      // trop profond
    [InlineData("playerdeck/themes/atlas/d/stage1.txt", false)]
    [InlineData("playerdeck/themes/atlas/../x.json", false)]
    [InlineData("playerdeck/themes/../x.json", false)]
    [InlineData("playerdeck/themes/.x.json", false)]
    [InlineData("playerdeck/fonts/x.ttf", false)]
    [InlineData("playerdeck/x.json", false)]
    [InlineData("playerdeck/themes/", false)]
    [InlineData("playerdeck/themes/..\\x.json", false)]                 // un antislash : jamais admis
    [InlineData("tools/playerdeck/themes/x.json", false)]
    [InlineData("PlayerDeck/themes/x.json", false)]                     // la casse du depot est « playerdeck »
    public void Le_Data_Pack_n_admet_que_les_themes_et_les_cartes_des_Decks(string chemin, bool attendu)
        => Assert.Equal(attendu, DataPackPaths.Autorise(chemin, Dossiers));

    private static string Theme(string dossier, string id, string nom)
    {
        Directory.CreateDirectory(dossier);
        var f = Path.Combine(dossier, id + ".json");
        File.WriteAllText(f, "{ \"format\": \"nelfeplay.deck-theme/1\", \"id\": \"" + id + "\", \"name\": \"" + nom + "\", \"version\": 1,"
            + " \"match\": { \"roms\": [\"" + id + "\"] } }");
        return f;
    }

    [Fact]
    public void Le_catalogue_lit_le_theme_du_Data_Pack_avant_celui_de_tools_playerdeck()
    {
        var deck = Path.Combine(_racine, "tools", "playerdeck");
        var dataPack = Path.Combine(_racine, "resources", "playerdeck", "themes");
        Theme(Path.Combine(deck, "themes"), "commun", "LIVRE");
        var duDataPack = Theme(dataPack, "commun", "DATAPACK");

        var c = DeckCatalogue.Lire(deck, dataPack);

        var e = c.Trouver("commun")!;
        Assert.Equal("DATAPACK", e.Name);
        Assert.Equal(duDataPack, e.File);
        Assert.True(e.Official);
        Assert.Single(c.Tous, x => x.Id == "commun");
    }

    [Fact]
    public void Un_theme_absent_du_Data_Pack_reste_celui_de_tools_playerdeck()
    {
        var deck = Path.Combine(_racine, "tools", "playerdeck");
        var dataPack = Path.Combine(_racine, "resources", "playerdeck", "themes");
        var livre = Theme(Path.Combine(deck, "themes"), "seulement-livre", "LIVRE");
        Theme(dataPack, "seulement-datapack", "DATAPACK");

        var c = DeckCatalogue.Lire(deck, dataPack);

        Assert.Equal(livre, c.Trouver("seulement-livre")!.File);
        Assert.NotNull(c.Trouver("seulement-datapack"));
        Assert.True(c.Trouver("seulement-livre")!.Official);
    }

    [Fact]
    public void Sans_dossier_du_Data_Pack_le_catalogue_est_celui_d_avant()
    {
        var deck = Path.Combine(_racine, "tools", "playerdeck");
        Theme(Path.Combine(deck, "themes"), "livre", "LIVRE");
        Assert.Equal(["livre"], DeckCatalogue.Lire(deck).Tous.Select(e => e.Id).ToArray());
        Assert.Equal(["livre"], DeckCatalogue.Lire(deck, Path.Combine(_racine, "absent")).Tous.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Le_Deck_recoit_le_dossier_du_Data_Pack_quand_il_porte_des_themes_et_rien_sinon()
    {
        var dataPack = Path.Combine(_racine, "resources", "playerdeck", "themes");
        Assert.Equal("", DeckCatalogue.OptionThemesDuDataPack(dataPack));      // dossier absent
        Directory.CreateDirectory(dataPack);
        Assert.Equal("", DeckCatalogue.OptionThemesDuDataPack(dataPack));      // vide
        Directory.CreateDirectory(Path.Combine(dataPack, "atlas", "d"));
        File.WriteAllText(Path.Combine(dataPack, "atlas", "d", "stage1.json"), "{}");
        Assert.Equal("", DeckCatalogue.OptionThemesDuDataPack(dataPack));      // des cartes sans theme : rien a prioriser
        Theme(dataPack, "x", "X");
        Assert.Equal($" --themes-dir \"{dataPack}\"", DeckCatalogue.OptionThemesDuDataPack(dataPack));
    }

    [Fact]
    public void Le_dossier_du_Data_Pack_est_sous_resources_de_la_racine_de_l_API()
        => Assert.Equal(Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.PluginRoot, "resources", "playerdeck", "themes"), DeckCatalogue.DossierDataPack);
}
