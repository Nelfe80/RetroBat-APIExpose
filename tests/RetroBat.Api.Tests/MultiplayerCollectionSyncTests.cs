using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La collection NELFEPLAY MULTIPLAYER sans reseau ni vraie ROM : l'arcade entre par son set,
/// la console par son contenu, et un catalogue absent n'efface rien.
/// </summary>
public class MultiplayerCollectionSyncTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "multiplayer-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FauxReglages _reglages = new();
    private readonly NelfePlayOptions _nelfeplay = new() { ShowMultiplayerCollection = true };

    private string Roms => Path.Combine(_racine, "roms");

    private string Etat => Path.Combine(_racine, "state");

    private string CataloguePath => Path.Combine(_racine, "resources", "multiplayer", "catalogue.json");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private EsCustomCollectionWriter Writer() => new(
        _reglages, logger: null,
        collectionsRoot: Path.Combine(_racine, "collections"),
        stateRoot: Etat);

    private MultiplayerCollectionSyncService Service() => new(
        new FauxOptions(new ApiExposeOptions { NelfePlay = _nelfeplay }),
        new MultiplayerCatalogueStore(logger: null, path: CataloguePath),
        new InstalledGameCatalog(new SansDefinition(), logger: null, romsRoot: Roms, ramRoot: Path.Combine(_racine, "ram")),
        Writer(),
        new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"),
            sourceRoot: Path.Combine(_racine, "assets"), stateRoot: Etat),
        new MediaRuntimeState(),
        logger: null,
        romsRoot: Roms,
        emulateurTourne: () => false);

    private string PoserFichier(string systeme, string nom, string contenu = "rom")
    {
        var dossier = Path.Combine(Roms, systeme);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nom);
        File.WriteAllText(chemin, contenu);
        return chemin;
    }

    /// <summary>Une cartouche zippee : une seule entree, dont on rend le md5 et le CRC.</summary>
    private (string Chemin, string Md5, string Crc) PoserCartouche(string systeme, string nomZip, string nomEntree, string contenu)
    {
        var dossier = Path.Combine(Roms, systeme);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nomZip);
        var octets = Encoding.UTF8.GetBytes(contenu);
        using (var zip = ZipFile.Open(chemin, ZipArchiveMode.Create))
        {
            using var flux = zip.CreateEntry(nomEntree).Open();
            flux.Write(octets);
        }

        string crc;
        using (var zip = ZipFile.OpenRead(chemin))
        {
            crc = zip.Entries[0].Crc32.ToString("x8");
        }

        return (chemin, Convert.ToHexString(MD5.HashData(octets)).ToLowerInvariant(), crc);
    }

    private void PoserCatalogue(object catalogue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CataloguePath)!);
        File.WriteAllText(CataloguePath, JsonSerializer.Serialize(catalogue));
    }

    private static object Arcade(string set, int joueurs = 2) => new
    {
        id = "arcade/" + set, system = "arcade", set, name = set, family = "coop", players = joueurs, level = "classique",
    };

    private static object JeuConsole(string id, string systeme, string md5, string? crc = null) => new
    {
        id, system = systeme, content = new { md5 = new[] { md5 }, crc = crc == null ? Array.Empty<string>() : new[] { crc } },
        name = id, family = "course", players = 4, level = "classique",
    };

    private static object Catalogue(params object[] jeux) => new
    {
        schema_version = 1,
        revision = "test",
        cores = new Dictionary<string, object>
        {
            ["arcade"] = new { core = "fbneo" },
            ["gba"] = new
            {
                core = "mgba_splitscreen",
                options = new Dictionary<string, string> { ["splitscreen_boot_menu"] = "off" },
                seat_options = new Dictionary<string, string> { ["splitscreen_focus_player"] = "{seat}" },
            },
        },
        games = jeux,
    };

    private HashSet<string> Collection()
    {
        var chemin = Writer().ConfigPath(MultiplayerCollectionSyncService.CollectionName);
        return File.Exists(chemin)
            ? File.ReadAllLines(chemin).Where(ligne => ligne.Length > 0).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    [Fact]
    public async Task Un_set_d_arcade_entre_par_son_nom_et_fbneo_passe_avant_mame()
    {
        PoserFichier("mame", "mslug3.zip");
        var fbneo = PoserFichier("fbneo", "mslug3.zip");
        var seulMame = PoserFichier("mame", "sf2ce.zip");
        PoserFichier("fbneo", "mslug3x.zip");
        PoserCatalogue(Catalogue(Arcade("mslug3"), Arcade("sf2ce"), Arcade("absent")));

        var service = Service();
        var statut = await service.SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("ok", statut.State);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fbneo, seulMame }, Collection());
        Assert.Equal(["arcade/absent"], statut.Missing);
        Assert.Equal("fbneo", service.Games.Single(jeu => jeu.Game.Id == "arcade/mslug3").Core?.Core);
    }

    [Fact]
    public async Task Une_cartouche_entre_par_son_contenu_jamais_par_son_nom()
    {
        var bonne = PoserCartouche("gba", "nom quelconque.zip", "x.gba", "mario kart usa");
        var trompeuse = PoserCartouche("gba", "Mario Kart - Super Circuit (USA).zip", "y.gba", "un autre dump");
        PoserCatalogue(Catalogue(JeuConsole("gba/mksc", "gba", bonne.Md5, bonne.Crc)));

        var service = Service();
        await service.SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bonne.Chemin }, Collection());
        Assert.DoesNotContain(trompeuse.Chemin, Collection());
        var jeu = service.Games.Single();
        Assert.Equal("mgba_splitscreen", jeu.Core?.Core);
        Assert.Equal("off", jeu.Core?.Options?["splitscreen_boot_menu"]);
        Assert.Equal("{seat}", jeu.Core?.SeatOptions?["splitscreen_focus_player"]);
    }

    [Fact]
    public async Task Avec_deux_versions_acceptees_la_preferee_du_catalogue_l_emporte()
    {
        // L'europeenne arrive la premiere dans le dossier, mais l'USA est en tete du catalogue.
        var europe = PoserCartouche("gba", "a (Europe).zip", "a.gba", "version europe");
        var usa = PoserCartouche("gba", "b (USA).zip", "b.gba", "version usa");
        PoserCatalogue(Catalogue(new
        {
            id = "gba/jeu", system = "gba", name = "Jeu", family = "course", players = 4, level = "classique",
            content = new { md5 = new[] { usa.Md5, europe.Md5 }, crc = new[] { usa.Crc, europe.Crc } },
        }));

        await Service().SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { usa.Chemin }, Collection());
    }

    [Fact]
    public void Le_prefiltre_ecarte_un_zip_dont_le_crc_differe_sans_le_decompresser()
    {
        var cartouche = PoserCartouche("gba", "a.zip", "a.gba", "contenu");
        Assert.True(MultiplayerCollectionSyncService.PassePrefiltre(cartouche.Chemin, new MultiplayerContent { Crc = [cartouche.Crc] }));
        Assert.False(MultiplayerCollectionSyncService.PassePrefiltre(cartouche.Chemin, new MultiplayerContent { Crc = ["00000000"] }));

        var nu = PoserFichier("gba", "b.gba", "1234");
        Assert.True(MultiplayerCollectionSyncService.PassePrefiltre(nu, new MultiplayerContent { Size = [4] }));
        Assert.False(MultiplayerCollectionSyncService.PassePrefiltre(nu, new MultiplayerContent { Size = [8] }));
    }

    [Fact]
    public async Task Un_catalogue_absent_ou_refuse_n_efface_pas_la_collection()
    {
        var fbneo = PoserFichier("fbneo", "mslug3.zip");
        PoserCatalogue(Catalogue(Arcade("mslug3")));
        var service = Service();
        await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.Contains(fbneo, Collection());

        File.WriteAllText(CataloguePath, "{\"schema_version\": 9, \"games\": []}");
        var statut = await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.Equal("stale", statut.State);
        Assert.Contains(fbneo, Collection());

        File.Delete(CataloguePath);
        statut = await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.Equal("stale", statut.State);
        Assert.Contains(fbneo, Collection());
    }

    [Fact]
    public async Task Couper_l_option_retire_la_collection()
    {
        PoserFichier("fbneo", "mslug3.zip");
        PoserCatalogue(Catalogue(Arcade("mslug3")));
        var service = Service();
        await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.NotEmpty(Collection());

        _nelfeplay.ShowMultiplayerCollection = false;
        var statut = await service.SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("disabled", statut.State);
        Assert.Empty(Collection());
        Assert.Empty(service.Games);
    }

    [Fact]
    public void Un_jeu_a_un_moniteur_par_joueur_donne_son_ecran_a_chaque_place_et_fige_son_option()
    {
        var catalogue = new MultiplayerCatalogue
        {
            SchemaVersion = 1,
            Cores = new Dictionary<string, MultiplayerCore>
            {
                ["arcade"] = new() { Core = "fbneo", Options = new Dictionary<string, string> { ["fbneo-commun"] = "a" } },
            },
        };
        var wrally2 = new MultiplayerGame
        {
            Id = "arcade/wrally2", System = "arcade", Set = "wrally2", Players = 2, Screens = 2, SeatScreens = [1, 2],
            Options = new Dictionary<string, string> { ["fbneo-dipswitch-wrally2-Monitors"] = "Two", ["fbneo-commun"] = "b" },
        };
        var harddunk = new MultiplayerGame { Id = "arcade/harddunk", System = "arcade", Set = "harddunk", Players = 6, Screens = 2 };

        var coeur = catalogue.CoreOf(wrally2)!;
        Assert.Equal("fbneo", coeur.Core);
        Assert.Equal("Two", coeur.Options!["fbneo-dipswitch-wrally2-Monitors"]);
        Assert.Equal("b", coeur.Options["fbneo-commun"]);
        Assert.Equal("a", catalogue.CoreOf(harddunk)!.Options!["fbneo-commun"]);
        Assert.Equal([1, 2], new[] { wrally2.EcranDeLaPlace(1), wrally2.EcranDeLaPlace(2) });
        // Sans table : trois places par ecran, dans l'ordre.
        Assert.Equal([1, 1, 1, 2, 2, 2], Enumerable.Range(1, 6).Select(harddunk.EcranDeLaPlace).ToArray());
        Assert.Equal(1, new MultiplayerGame { Players = 4 }.EcranDeLaPlace(3));
    }

    [Fact]
    public async Task Un_jeu_retenu_est_inscrit_dans_sa_gamelist_a_la_premiere_passe_et_quand_la_collection_change()
    {
        // Mario Kart (23/10) : la gamelist de la borne ne nommait que la version europeenne, le catalogue preferait l'USA ;
        // avec ParseGamelistOnly, ES n'affichait pas le jeu de la collection.
        var usa = PoserCartouche("gba", "Mario Kart - Super Circuit (USA).zip", "a.gba", "version usa");
        var arcade = PoserFichier("fbneo", "mslug3.zip");
        PoserCatalogue(Catalogue(JeuConsole("gba/mario-kart-super-circuit", "gba", usa.Md5, usa.Crc), Arcade("mslug3")));
        var service = Service();
        var appels = new List<(string Systeme, List<string> Chemins)>();
        service.Declarer = (systeme, jeux) =>
        {
            appels.Add((systeme, jeux.Select(j => Path.GetFullPath(j.CheminAbsolu)).ToList()));
            return jeux.Count;
        };

        await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.Equal(["fbneo", "gba"], appels.Select(a => a.Systeme).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(Path.GetFullPath(usa.Chemin), appels.Single(a => a.Systeme == "gba").Chemins);
        Assert.Contains(Path.GetFullPath(arcade), appels.Single(a => a.Systeme == "fbneo").Chemins);

        // Une passe sans changement ne relit pas les gamelists ; un jeu de plus, si.
        appels.Clear();
        await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.Empty(appels);
        PoserFichier("fbneo", "sf2ce.zip");
        PoserCatalogue(Catalogue(JeuConsole("gba/mario-kart-super-circuit", "gba", usa.Md5, usa.Crc), Arcade("mslug3"), Arcade("sf2ce")));
        await service.SynchroniserAsync("test", CancellationToken.None);
        Assert.NotEmpty(appels);
    }

    [Fact]
    public void Par_defaut_la_collection_est_coupee()
        => Assert.False(new NelfePlayOptions().ShowMultiplayerCollection);

    [Theory]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"a\",\"system\":\"arcade\",\"name\":\"A\",\"players\":2}]}", "set manquant")]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"g\",\"system\":\"gba\",\"name\":\"G\",\"players\":4}]}", "aucun md5")]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"a\",\"system\":\"arcade\",\"set\":\"a\",\"players\":1}]}", "moins de deux joueurs")]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"a\",\"system\":\"arcade\",\"set\":\"a\",\"players\":2},{\"id\":\"A\",\"system\":\"arcade\",\"set\":\"b\",\"players\":2}]}", "en double")]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"a\",\"system\":\"arcade\",\"set\":\"a\",\"players\":2,\"level\":\"pro\"}]}", "niveau")]
    [InlineData("{\"schema_version\":2,\"games\":[]}", "schema 2")]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"a\",\"system\":\"arcade\",\"set\":\"a\",\"players\":2,\"screens\":2,\"seat_screens\":[1,3]}]}", "ecrans par place")]
    [InlineData("{\"schema_version\":1,\"games\":[{\"id\":\"a\",\"system\":\"arcade\",\"set\":\"a\",\"players\":6,\"screens\":2,\"seat_screens\":[1,2]}]}", "ecrans par place")]
    public void Un_catalogue_douteux_est_refuse(string json, string raison)
    {
        var catalogue = JsonSerializer.Deserialize<MultiplayerCatalogue>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Contains(raison, MultiplayerCatalogueStore.Valider(catalogue));
    }

    [Fact]
    public void Le_catalogue_du_data_pack_est_sain()
    {
        // Le vrai fichier, quand le test tourne dans l'arbre des depots : il part sur toute la flotte.
        var depot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "RetroBat-DataPack", "multiplayer", "catalogue.json"));
        if (!File.Exists(depot))
        {
            return;
        }

        var catalogue = new MultiplayerCatalogueStore(logger: null, path: depot).Lire(out var erreur);
        Assert.Null(erreur);
        Assert.NotNull(catalogue);
        Assert.All(catalogue!.Games, jeu => Assert.NotNull(catalogue.CoreOf(jeu)));

        // Chaque image citee est un chemin admis, et le fichier est dans le depot : une image oubliee laisserait la borne
        // au badge de secours sans que personne le voie.
        var dossier = Path.GetDirectoryName(depot)!;
        var images = catalogue.Games.SelectMany(jeu => jeu.Choices ?? []).SelectMany(choix => choix.Values).Where(v => v.Image is not null).ToList();
        Assert.All(images, v => Assert.True(v.CheminDeLImage(dossier) is { } chemin && File.Exists(chemin), v.Image));

        // Chaque etat de depart est livre, a l'empreinte du catalogue : sinon le depart de ce jeu s'annulerait partout.
        var departs = catalogue.Games.SelectMany(jeu => (jeu.Starts ?? []).Prepend(jeu.Start)).OfType<MultiplayerStart>().ToList();
        Assert.All(departs, depart =>
        {
            var fichier = Path.Combine(dossier, depart.State.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fichier), depart.State);
            Assert.Equal(depart.Sha256.Trim().ToUpperInvariant(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fichier))));
        });
    }

    [Theory]
    [InlineData("images/arcade/sf2ce/character/ryu.png", true)]
    [InlineData("images/arcade/sf2ce/character/RYU.PNG", true)]
    [InlineData("images/../states/arcade/sf2ce/selection-2p-fbneo.state", false)]
    [InlineData("images/arcade/sf2ce/character/ryu.state", false)]
    [InlineData("states/arcade/sf2ce/ryu.png", false)]
    [InlineData("/images/arcade/ryu.png", false)]
    [InlineData("C:/images/ryu.png", false)]
    [InlineData("images//ryu.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void L_image_d_un_choix_reste_sous_images(string? image, bool admise)
    {
        var dossier = Path.Combine(_racine, "resources", "multiplayer");
        var chemin = new MultiplayerChoiceValue { Name = "RYU", Image = image }.CheminDeLImage(dossier);
        Assert.Equal(admise, chemin is not null);
        if (chemin is not null)
        {
            Assert.StartsWith(Path.Combine(dossier, "images") + Path.DirectorySeparatorChar, chemin);
        }
    }

    private sealed class SansDefinition : IScoreSlugResolver
    {
        public string? ResolveScoreSlug(string systemId, string? romFileName, string? md5, string? cheevosHash) => null;
    }

    private sealed class FauxOptions(ApiExposeOptions valeur) : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = valeur;

        public ApiExposeOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ApiExposeOptions, string?> listener) => new Rien();

        private sealed class Rien : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FauxReglages : IEsSettingsStore
    {
        private readonly XDocument _document = new(new XElement("config"));

        public string? Valeur(string cle) => _document.Root!
            .Elements()
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, cle, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;

        public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default)
            => update(_document);

        public IReadOnlyDictionary<string, string> ReadAllSettings() => _document.Root!
            .Elements()
            .Where(e => e.Attribute("name") != null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")?.Value ?? string.Empty);

        public EmulationStationSettingsSnapshot ReadSnapshot() => new(string.Empty, DateTime.UtcNow, ReadAllSettings());

        public void Invalidate() { }

        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
