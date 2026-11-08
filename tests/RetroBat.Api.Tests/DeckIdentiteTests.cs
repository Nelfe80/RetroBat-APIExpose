using RetroBat.Api.Deck;
using RetroBat.Api.Media;
using RetroBat.Domain.Paths;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'IDENTITE DU JEU D'UN LANCEMENT AVEC DECK (essai reel du 08/11) : en arcade, la fiche par nom de fichier donnait un
/// autre jeu (mslug3 : « 2020 Super Baseball », sf2ce : « Adventure Quiz Capcom World 2 »). La cle vient du .MEM qui
/// connait le dump, comme la route /api/v1/gamelists/resolve ; le nom du fichier ne sert qu'en repli.
/// </summary>
public sealed class DeckIdentiteTests
{
    private static readonly Dictionary<string, string> SlugsDesMem = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mslug3"] = "metal-slug-3",
        ["sf2ce"] = "street-fighter-ii-champion-edition",
    };

    private static string? Slug(string systeme, string chemin) => SlugsDesMem.GetValueOrDefault(Path.GetFileNameWithoutExtension(chemin));

    // la fiche par nom de fichier telle que la borne l'a donnee : un autre jeu
    private static (string, string?)? FicheFausse(string systeme, string chemin) => Path.GetFileNameWithoutExtension(chemin) switch
    {
        "mslug3" => ("arcade/2020-super-baseball", "arcade"),
        "sf2ce" => ("arcade/adventure-quiz-capcom-world-2", "arcade"),
        _ => ("arcade/autre-jeu", "arcade"),
    };

    [Theory]
    [InlineData(@"E:\RetroBat\roms\fbneo\mslug3.zip", "arcade/metal-slug-3")]
    [InlineData(@"E:\RetroBat\roms\fbneo\sf2ce.zip", "arcade/street-fighter-ii-champion-edition")]
    public void La_cle_d_un_jeu_d_arcade_vient_de_son_MEM_jamais_de_la_fiche_par_nom(string chemin, string attendu)
    {
        var cle = DeckIdentite.Resoudre("fbneo", chemin, null, Slug, null, FicheFausse);
        Assert.Equal(attendu, cle.Jeu);
        Assert.Equal("mem-score", cle.Source);
        Assert.Equal(attendu["arcade/".Length..], cle.Groupe);
    }

    [Fact]
    public void Sans_MEM_le_groupe_de_l_appelant_puis_le_nom_du_fichier_jamais_la_fiche_en_arcade()
    {
        Assert.Equal(("arcade/metal-slug-3", "caller"), Paire(DeckIdentite.Resoudre("fbneo", @"E:\roms\fbneo\mslug3.zip", "metal-slug-3", null, null, FicheFausse)));
        Assert.Equal(("arcade/mslug3", "file"), Paire(DeckIdentite.Resoudre("fbneo", @"E:\roms\fbneo\mslug3.zip", null, null, null, FicheFausse)));
        // hors arcade, la fiche du referentiel reste un repli avant le nom du fichier
        Assert.Equal(("megadrive/sonic-the-hedgehog", "catalog"),
            Paire(DeckIdentite.Resoudre("megadrive", @"E:\roms\megadrive\Sonic (USA).md", null, null, null, (_, _) => ("megadrive/sonic-the-hedgehog", "megadrive"))));
        // un .MEM sans score (jeu ouvert aux seules epreuves) identifie aussi le jeu
        Assert.Equal(("snes/super-metroid", "mem-definition"),
            Paire(DeckIdentite.Resoudre("snes", @"E:\roms\snes\sm.sfc", null, null, (_, _) => "super-metroid", null)));
    }

    private static (string, string) Paire(DeckIdentite.Cle c) => (c.Jeu, c.Source);

    [Fact]
    public void Le_referentiel_reel_reconnait_mslug3_et_sf2ce_par_leur_MEM()
    {
        // seulement si les .MEM officiels sont la (poste du user, borne) : sinon rien a verifier ici
        var dossier = Path.Combine(RetroBatPaths.RamResourcesRoot, "arcade");
        if (!File.Exists(Path.Combine(dossier, "metal-slug-3.MEM")) || !File.Exists(Path.Combine(dossier, "street-fighter-ii-champion-edition.MEM"))) return;
        var canonique = new RomCanonicalResolver();
        Assert.Equal("arcade/metal-slug-3", DeckIdentite.Resoudre(canonique, "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip", null).Jeu);
        Assert.Equal("arcade/street-fighter-ii-champion-edition", DeckIdentite.Resoudre(canonique, "fbneo", @"E:\RetroBat\roms\fbneo\sf2ce.zip", null).Jeu);
        Assert.Equal("arcade/19xx-the-war-against-destiny", DeckIdentite.Resoudre(canonique, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip", null).Jeu);
    }
}
