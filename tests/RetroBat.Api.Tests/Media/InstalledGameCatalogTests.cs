using System.Security.Cryptography;
using System.Text;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Ce que la borne possede vraiment. Une collection batie sur une gamelist optimiste pointe des
/// fichiers absents ; une collection qui ignore les fichiers pas encore inscrits en gamelist
/// rate des jeux tout juste copies. Les deux cas sont couverts ici.
/// </summary>
public class InstalledGameCatalogTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "inventaire-" + Guid.NewGuid().ToString("N")[..8]);

    private string Roms => Path.Combine(_racine, "roms");

    private string Ram => Path.Combine(_racine, "ram");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private InstalledGameCatalog Catalogue(FauxResolveur? resolveur = null)
        => new(resolveur ?? new FauxResolveur(), logger: null, romsRoot: Roms, ramRoot: Ram);

    private string PoserRom(string systeme, string nom, string contenu = "rom")
    {
        var dossier = Path.Combine(Roms, systeme);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nom);
        File.WriteAllText(chemin, contenu);
        return chemin;
    }

    private void PoserGamelist(string systeme, string contenu)
    {
        var dossier = Path.Combine(Roms, systeme);
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "gamelist.xml"), contenu);
    }

    private string PoserMem(string systemeCanonique, string slug, string contenu)
    {
        var dossier = Path.Combine(Ram, systemeCanonique);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, slug + ".MEM");
        File.WriteAllText(chemin, contenu);
        return chemin;
    }

    [Fact]
    public void Un_jeu_inscrit_en_gamelist_mais_absent_du_disque_ne_compte_pas()
    {
        PoserRom("megadrive", "Sonic The Hedgehog (Europe).zip");
        PoserGamelist("megadrive", """
            <gameList>
              <game><path>./Sonic The Hedgehog (Europe).zip</path><name>Sonic</name><md5>1a3b</md5></game>
              <game><path>./Streets of Rage (Europe).zip</path><name>Streets of Rage</name></game>
            </gameList>
            """);

        var jeux = Catalogue().Enumerate();

        Assert.Single(jeux);
        Assert.Equal("Sonic", jeux[0].DisplayName);
        Assert.True(jeux[0].FromGamelist);
        Assert.Equal("1a3b", jeux[0].Md5);
    }

    [Fact]
    public void Un_fichier_pas_encore_en_gamelist_compte_aussi()
    {
        PoserRom("megadrive", "Sonic The Hedgehog (Europe).zip");
        PoserRom("megadrive", "Tout Juste Copie.zip");
        PoserGamelist("megadrive", """
            <gameList>
              <game><path>./Sonic The Hedgehog (Europe).zip</path><name>Sonic</name></game>
            </gameList>
            """);

        var jeux = Catalogue().Enumerate();

        Assert.Equal(2, jeux.Count);
        Assert.Contains(jeux, jeu => !jeu.FromGamelist && jeu.DisplayName == "Tout Juste Copie");
    }

    [Fact]
    public void Sans_gamelist_les_fichiers_font_foi()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserRom("fbneo", "notice.txt");

        var jeux = Catalogue().Enumerate();

        Assert.Single(jeux);
        Assert.Equal("19xx", jeux[0].DisplayName);
    }

    [Fact]
    public void Une_gamelist_illisible_ne_cache_pas_les_jeux()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserGamelist("fbneo", "<gameList><game><path>tronqu");

        Assert.Single(Catalogue().Enumerate());
    }

    [Fact]
    public void Mame_et_fbneo_rejoignent_le_systeme_arcade()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserRom("mame", "mslug.zip");
        PoserRom("megadrive", "sonic.zip");

        var arcade = Catalogue().Enumerate(new[] { "arcade" });

        Assert.Equal(2, arcade.Count);
        Assert.All(arcade, jeu => Assert.Equal("arcade", jeu.CanonicalSystemId));
        Assert.Equal(new[] { "fbneo", "mame" }, arcade.Select(jeu => jeu.FrontendSystemId).OrderBy(id => id));
    }

    [Fact]
    public void Ne_parcourt_que_les_systemes_demandes()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserRom("megadrive", "sonic.zip");
        PoserRom("nes", "contra.zip");

        var jeux = Catalogue().Enumerate(new[] { "megadrive" });

        Assert.Single(jeux);
        Assert.Equal("megadrive", jeux[0].FrontendSystemId);
    }

    [Fact]
    public void Un_jeu_sans_definition_de_score_n_est_pas_scorable()
    {
        PoserRom("megadrive", "sonic.zip");
        var resolveur = new FauxResolveur(); // ne connait rien

        var jeux = Catalogue(resolveur).Enumerate();

        Assert.False(jeux[0].ScorableLocal);
        Assert.Equal(string.Empty, jeux[0].RomGroup);
        Assert.Null(jeux[0].OfficialDefinitionPath);
    }

    [Fact]
    public void Le_groupe_et_la_definition_officielle_viennent_du_resolveur()
    {
        PoserRom("fbneo", "19xx.zip");
        var attendu = PoserMem("arcade", "19xx", "SCORE_STATE");
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };

        var jeux = Catalogue(resolveur).Enumerate(new[] { "arcade" });

        Assert.True(jeux[0].ScorableLocal);
        Assert.Equal("19xx", jeux[0].RomGroup);
        Assert.Equal(attendu, jeux[0].OfficialDefinitionPath);
    }

    [Fact]
    public void L_empreinte_de_la_definition_officielle_est_celle_du_fichier()
    {
        const string contenu = "SCORE_STATE 0x1234";
        PoserMem("arcade", "19xx", contenu);
        var attendue = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contenu))).ToLowerInvariant();

        Assert.Equal(attendue, Catalogue().OfficialDefinitionSha256("arcade", "19xx"));
    }

    [Fact]
    public void Une_definition_perso_ne_remplace_jamais_l_officielle()
    {
        PoserMem("arcade", "19xx", "OFFICIELLE");
        var perso = Path.Combine(Ram, ".user", "arcade");
        Directory.CreateDirectory(perso);
        File.WriteAllText(Path.Combine(perso, "19xx.MEM"), "PERSO");

        var officielle = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("OFFICIELLE"))).ToLowerInvariant();
        Assert.Equal(officielle, Catalogue().OfficialDefinitionSha256("arcade", "19xx"));
    }

    [Fact]
    public void Une_definition_absente_rend_une_empreinte_vide()
    {
        Assert.Equal(string.Empty, Catalogue().OfficialDefinitionSha256("arcade", "inconnu"));
    }

    [Fact]
    public void Un_dossier_roms_absent_rend_une_liste_vide()
    {
        Assert.Empty(Catalogue().Enumerate());
    }

    [Theory]
    [InlineData("./Sonic.zip", "Sonic.zip")]
    [InlineData("Sonic.zip", "Sonic.zip")]
    [InlineData("./sous/dossier/Sonic.zip", "sous/dossier/Sonic.zip")]
    public void Les_chemins_de_gamelist_sont_resolus_par_rapport_au_systeme(string brut, string attenduRelatif)
    {
        var systemRoot = Path.Combine(Roms, "megadrive");
        var resolu = InstalledGameCatalog.ResolveAbsolutePath(systemRoot, brut);
        Assert.Equal(Path.GetFullPath(Path.Combine(systemRoot, attenduRelatif.Replace('/', Path.DirectorySeparatorChar))), resolu);
    }

    [Fact]
    public void Les_alias_arcade_ont_une_seule_table()
    {
        Assert.Equal("arcade", RomCanonicalResolver.CanonicalScoringSystem("MAME64"));
        Assert.Equal("arcade", RomCanonicalResolver.CanonicalScoringSystem("fbneo"));
        Assert.Equal("megadrive", RomCanonicalResolver.CanonicalScoringSystem("megadrive"));
        Assert.Contains("mame", RomCanonicalResolver.FrontendSystemsFor("arcade"));
        Assert.Equal(new[] { "nes" }, RomCanonicalResolver.FrontendSystemsFor("NES"));
        Assert.Empty(RomCanonicalResolver.FrontendSystemsFor(""));
    }

    /// <summary>Le referentiel .MEM en memoire : le vrai lit resources/ram, hors de portee d'un test.</summary>
    private sealed class FauxResolveur : IScoreSlugResolver
    {
        private readonly Dictionary<string, string> _slugs = new(StringComparer.OrdinalIgnoreCase);

        public string this[string romFileName]
        {
            set => _slugs[romFileName] = value;
        }

        public string? ResolveScoreSlug(string systemId, string? romFileName, string? md5, string? cheevosHash)
            => romFileName is { Length: > 0 } && _slugs.TryGetValue(romFileName, out var slug) ? slug : null;
    }
}
