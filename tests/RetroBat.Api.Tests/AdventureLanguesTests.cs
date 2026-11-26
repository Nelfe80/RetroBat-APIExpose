using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Adventure;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// NELFEPLAY ADVENTURE 1.9.223 : la langue du depart (langues melees), la couverture partielle des nombres de joueurs, le nom
/// <c>gba_srm_console1</c>. Sur le VRAI catalogue publie du Data Pack (701d9f3d, lecture seule) et sur des variantes.
/// </summary>
public class AdventureLanguesTests : IDisposable
{
    private const string CoeurGba = "cf385d9421d5ad130cd854adc466444ffd84baed2c375630ab0afa84be8eca44";
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "adv-langues-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string? CatalogueDuDataPack()
    {
        var env = Environment.GetEnvironmentVariable("NELFEPLAY_DATAPACK");
        var dossier = !string.IsNullOrEmpty(env) ? env : @"E:\RetroBat\plugins\RetroBat-DataPack";
        var chemin = Path.Combine(dossier, "adventure", "catalogue.json");
        return File.Exists(chemin) ? chemin : null;
    }

    private static AdventureJoueurLangue J(int place, string? langue, bool hote = false)
        => new(place, hote, AdventureLangues.LanguesDe(langue));

    // ── la langue d'un joueur ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("fr_FR", "fr")]
    [InlineData("en-US", "en")]
    [InlineData("DE", "de")]
    [InlineData("pt_BR", "pt")]
    [InlineData("French", "fr")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("zzzzzz", null)]
    public void La_langue_d_ES_se_ramene_a_deux_lettres(string? entree, string? attendu)
        => Assert.Equal(attendu, AdventureLangues.Normaliser(entree));

    // ── le vrai catalogue publie ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Le_vrai_catalogue_publie_se_lit_en_entier_et_chaque_version_a_un_depart_a_chaque_nombre()
    {
        var chemin = CatalogueDuDataPack();
        if (chemin is null)
        {
            return;
        }

        var store = new AdventureCatalogueStore(path: chemin);
        var entrees = store.Entrees(out var erreur);
        Assert.Null(erreur);
        Assert.Equal(6, entrees.Count);
        var dossier = Path.GetDirectoryName(chemin)!;
        foreach (var x in entrees)
        {
            Assert.True(x.Jeu.PartialStarts);
            foreach (var v in x.Spec.Versions)
            {
                // Les departs par langue : fichier present a son empreinte, fait sur le contenu de SA version et pour le coeur GBA.
                foreach (var (langue, departs) in v.LanguageStarts ?? new Dictionary<string, AdventureDepartsDeLangue>())
                {
                    Assert.Equal(AdventureLangues.Normaliser(langue), langue);
                    Assert.NotEqual(v.LangueParDefaut, langue);
                    foreach (var d in departs.Tous())
                    {
                        var fichier = Path.Combine(dossier, d.State.Replace('/', Path.DirectorySeparatorChar));
                        Assert.Equal(d.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fichier))), ignoreCase: true);
                        Assert.Contains(d.Content!, v.Content.Md5);
                    }

                    for (var j = 2; j <= x.Jeu.Players; j++)
                    {
                        Assert.NotNull(departs.Pour(j, v.Content.Md5.Single(), CoeurGba));
                    }
                }
            }
        }
    }

    [Fact]
    public void Kirby_Europe_part_dans_la_langue_commune_a_chaque_nombre_de_joueurs()
    {
        var chemin = CatalogueDuDataPack();
        if (chemin is null)
        {
            return;
        }

        var kirby = new AdventureCatalogueStore(path: chemin).Entrees(out _).Single(e => e.Jeu.Id == "gba/kirby-amazing-mirror");
        var europe = kirby.Spec.Versions.Single(v => v.Key == "europe");
        var md5 = europe.Content.Md5.Single();
        Assert.Equal("en", europe.LangueParDefaut);
        Assert.Equal(["en", "de", "es", "fr", "it"], europe.LanguesAvecDepart().OrderBy(l => l == "en" ? "" : l).ToArray());

        // Deux francais : le depart FR a 2, 3 et 4.
        for (var n = 2; n <= 4; n++)
        {
            var choix = AdventureLangues.ChoisirLeDepart(europe, kirby.Jeu, n, md5, CoeurGba,
                Enumerable.Range(1, n).Select(p => J(p, "fr_FR", hote: p == 1)).ToList(), langueDeLAventure: null);
            Assert.Equal("fr", choix.Langue);
            Assert.EndsWith("-fr.state", choix.Depart!.State);
            Assert.Equal(n, choix.Depart.Players);
        }

        // Un allemand qui accueille un francais : pas de langue commune, l'anglais (depart par defaut, sans suffixe de langue).
        var mixte = AdventureLangues.ChoisirLeDepart(europe, kirby.Jeu, 2, md5, CoeurGba, [J(1, "de_DE", hote: true), J(2, "fr_FR")], null);
        Assert.Equal("en", mixte.Langue);
        Assert.EndsWith("aventure-2p-mgba-splitscreen.state", mixte.Depart!.State);

        // L'hote lit l'italien, l'invite inconnu (le site ne donne pas sa langue) : l'italien.
        var inconnu = AdventureLangues.ChoisirLeDepart(europe, kirby.Jeu, 2, md5, CoeurGba,
            [J(1, "it_IT", hote: true), new AdventureJoueurLangue(2, false, [])], null);
        Assert.Equal("it", inconnu.Langue);

        // Une langue que la version n'a pas (japonais) : anglais.
        var jp = AdventureLangues.ChoisirLeDepart(europe, kirby.Jeu, 3, md5, CoeurGba, [J(1, "ja_JP", true), J(2, "ja_JP"), J(3, "ja_JP")], null);
        Assert.Equal("en", jp.Langue);

        // Une aventure garde la langue de sa creation, meme si l'hote d'aujourd'hui en lit une autre.
        var garde = AdventureLangues.ChoisirLeDepart(europe, kirby.Jeu, 3, md5, CoeurGba, [J(1, "fr_FR", true), J(2, "fr_FR"), J(3, "fr_FR")], "es");
        Assert.Equal("es", garde.Langue);
        Assert.EndsWith("-es.state", garde.Depart!.State);

        // Un autre coeur que cf385d94 : aucun depart (etats stricts), partie a froid, dans n'importe quelle langue.
        var autreCoeur = AdventureLangues.ChoisirLeDepart(europe, kirby.Jeu, 2, md5, "04b8277e038b34429e8f667306388e6a0214b77c0b0c8f5d3663900c09b95a54", [J(1, "fr_FR", true), J(2, "fr_FR")], null);
        Assert.Null(autreCoeur.Depart);
    }

    [Fact]
    public void Une_version_monolingue_garde_son_depart_quelle_que_soit_la_langue_des_joueurs()
    {
        var chemin = CatalogueDuDataPack();
        if (chemin is null)
        {
            return;
        }

        var kirby = new AdventureCatalogueStore(path: chemin).Entrees(out _).Single(e => e.Jeu.Id == "gba/kirby-amazing-mirror");
        var usa = kirby.Spec.Versions.Single(v => v.Key == "usa");
        var md5 = usa.Content.Md5.Single();
        var choix = AdventureLangues.ChoisirLeDepart(usa, kirby.Jeu, 4, md5, CoeurGba, [J(1, "fr_FR", true), J(2, "fr_FR"), J(3, "de_DE"), J(4, "it_IT")], null);
        Assert.Equal("en", choix.Langue);
        Assert.Equal(4, choix.Depart!.Players);
        Assert.Equal(kirby.Jeu.DepartPour(4, md5, CoeurGba)!.State, choix.Depart.State);
    }

    [Fact]
    public void Chaque_jeu_et_chaque_version_du_vrai_catalogue_donne_un_depart_a_chaque_nombre_pour_toute_langue()
    {
        var chemin = CatalogueDuDataPack();
        if (chemin is null)
        {
            return;
        }

        foreach (var x in new AdventureCatalogueStore(path: chemin).Entrees(out _))
        {
            var coeur = x.Jeu.System == "gba" ? CoeurGba : null;
            foreach (var v in x.Spec.Versions)
            {
                var md5 = v.Content.Md5.Single();
                foreach (var langue in new[] { "en", "fr_FR", "de_DE", "es_ES", "it_IT", "ja_JP" })
                {
                    for (var n = 2; n <= x.Jeu.Players; n++)
                    {
                        var joueurs = Enumerable.Range(1, n).Select(p => J(p, langue, p == 1)).ToList();
                        var choix = AdventureLangues.ChoisirLeDepart(v, x.Jeu, n, md5, coeur, joueurs, null);
                        // Un jeu a mot de passe (Lost Vikings) n'a un depart qu'a 2 joueurs, qui est son seul nombre.
                        if (x.Jeu.DepartPour(n, md5, coeur) is not null)
                        {
                            Assert.NotNull(choix.Depart);
                        }

                        Assert.Contains(md5, new[] { choix.Depart?.Content ?? md5 });
                    }
                }
            }
        }
    }

    // ── la couverture partielle et les variantes ───────────────────────────────────────────────────

    private string EcrireUnCatalogue(object jeu, string? nom = null)
    {
        var chemin = Path.Combine(_racine, "resources", "adventure", nom ?? "catalogue.json");
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            revision = "t",
            cores = new Dictionary<string, object> { ["gba"] = new { core = "mgba_splitscreen", options = new Dictionary<string, string> { ["splitscreen_players"] = "{players}" } } },
            games = new[] { jeu },
        }));
        return chemin;
    }

    private static string Sha(string texte) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texte))).ToLowerInvariant();

    private object Depart(string nom, int joueurs, string contenu)
    {
        var fichier = Path.Combine(_racine, "resources", "adventure", "states", nom + ".state");
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        File.WriteAllText(fichier, nom);
        return new { state = "states/" + nom + ".state", sha256 = Sha(nom), players = joueurs, content = contenu };
    }

    /// <summary>Un jeu a quatre places, une version Europe multilingue dont les departs sont partiels : anglais a 2 et 3, francais a 2 seulement.</summary>
    private object JeuPartiel(bool avecKind = true) => new
    {
        id = "gba/jeu", system = "gba", name = "Jeu", family = "coop", players = new { min = 2, max = 4 }, core = "mgba_splitscreen", level = "nelfeplay",
        default_version = "eu",
        versions = new object[]
        {
            new
            {
                key = "eu", label = "Europe", languages = new[] { "en", "fr" }, start_language = "en", content = new { md5 = new[] { "eu1" } },
                start = Depart("eu-en-2", 2, "eu1"),
                starts = new[] { Depart("eu-en-3", 3, "eu1") },
                language_starts = new Dictionary<string, object> { ["fr"] = new { start = Depart("eu-fr-2", 2, "eu1") } },
            },
            new { key = "jp", label = "Japon", languages = new[] { "ja" }, content = new { md5 = new[] { "jp1" } } },
        },
        adventure = new { cartridge_save = new { console = 1, file = "{rom}.srm", kind = avecKind ? "gba_srm_console1" : "gba_srm_slots" } },
    };

    [Fact]
    public void Une_couverture_partielle_n_ecarte_plus_le_jeu_et_le_nombre_sans_depart_part_a_froid()
    {
        var store = new AdventureCatalogueStore(path: EcrireUnCatalogue(JeuPartiel()));
        var entrees = store.Entrees(out var erreur);
        Assert.Null(erreur);
        var jeu = Assert.Single(entrees).Jeu;
        Assert.NotNull(jeu.DepartPour(2, "eu1"));
        Assert.NotNull(jeu.DepartPour(3, "eu1"));
        Assert.Null(jeu.DepartPour(4, "eu1"));       // pas de 4 : a froid, les autres gardent leur depart
        Assert.Null(jeu.DepartPour(2, "jp1"));       // une version sans aucun depart : a froid aussi
    }

    [Fact]
    public void La_regle_de_MULTIPLAYER_reste_la_meme_hors_d_Adventure()
    {
        var jeu = new MultiplayerGame
        {
            Id = "gba/x", System = "gba", Name = "X", Players = 4, Level = MultiplayerLevels.NelfePlay,
            Content = new MultiplayerContent { Md5 = ["eu1"] },
            Start = new MultiplayerStart { State = "states/a.state", Sha256 = new string('a', 64), Players = 2, Content = "eu1" },
            Starts = [new MultiplayerStart { State = "states/b.state", Sha256 = new string('b', 64), Players = 3, Content = "eu1" }],
        };
        Assert.Equal("pas d'etat pour 4 joueurs", MultiplayerCatalogueStore.ValiderLeDepart(jeu));
        Assert.Null(MultiplayerCatalogueStore.ValiderLeDepart(jeu with { PartialStarts = true }));
    }

    [Fact]
    public void Les_departs_par_langue_se_lisent_et_une_langue_sans_le_nombre_part_a_froid_sans_changer_de_langue()
    {
        var store = new AdventureCatalogueStore(path: EcrireUnCatalogue(JeuPartiel()));
        var entree = Assert.Single(store.Entrees(out _));
        var version = entree.Spec.Versions.Single(v => v.Key == "eu");
        Assert.Equal("en", version.LangueParDefaut);
        Assert.Equal(["en", "fr"], version.LanguesAvecDepart());

        var francais = new[] { J(1, "fr_FR", true), J(2, "fr_FR"), J(3, "fr_FR") };
        // A 3 francais il n'y a pas de depart FR : ils lisent tous le francais, la partie part a froid (le choix de la langue se fait dans le jeu),
        // elle ne part pas en anglais.
        var trois = AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 3, "eu1", null, francais, null);
        Assert.Null(trois.Depart);
        // A 2, le depart FR existe.
        var deux = AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 2, "eu1", null, francais.Take(2).ToList(), null);
        Assert.Equal("fr", deux.Langue);
        Assert.EndsWith("eu-fr-2.state", deux.Depart!.State);
        // Une aventure en anglais a 3 : le depart anglais, meme avec des francais autour.
        Assert.Equal("en", AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 3, "eu1", null, francais, "en").Langue);
        // Une aventure en francais a 3 reste a froid.
        Assert.Null(AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 3, "eu1", null, francais, "fr").Depart);
    }

    [Fact]
    public void Gba_srm_console1_est_le_meme_format_que_gba_srm_slots()
    {
        foreach (var avecKind in new[] { true, false })
        {
            var spec = Assert.Single(new AdventureCatalogueStore(path: EcrireUnCatalogue(JeuPartiel(avecKind), "c" + avecKind + ".json")).Entrees(out _)).Spec;
            Assert.True(spec.Cartridge!.EstUnSrmGba);
        }

        Assert.False(new AdventureCartridge { Kind = "snes_sram" }.EstUnSrmGba);
    }

    [Fact]
    public void Un_depart_par_combinaison_de_langues_ne_sert_que_pour_la_suite_exacte_et_connue()
    {
        var combinaison = new
        {
            seat_languages = new[] { "fr", "de" },
            start = Depart("combo-fr-de", 2, "eu1"),
        };
        var jeu = new
        {
            id = "gba/jeu", system = "gba", name = "Jeu", family = "coop", players = new { min = 2, max = 2 }, core = "mgba_splitscreen", level = "nelfeplay",
            default_version = "eu",
            versions = new object[]
            {
                new
                {
                    key = "eu", languages = new[] { "en", "fr", "de" }, start_language = "en", content = new { md5 = new[] { "eu1" } },
                    start = Depart("eu-en-2", 2, "eu1"),
                    language_starts = new Dictionary<string, object> { ["fr"] = new { start = Depart("eu-fr-2", 2, "eu1") } },
                    combination_starts = new[] { combinaison },
                },
            },
        };
        var entree = Assert.Single(new AdventureCatalogueStore(path: EcrireUnCatalogue(jeu)).Entrees(out var erreur));
        Assert.Null(erreur);
        var version = entree.Spec.Versions.Single();

        // Console 1 en francais, console 2 en allemand : le depart de la combinaison.
        var combo = AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 2, "eu1", null, [J(1, "fr_FR", true), J(2, "de_DE")], null);
        Assert.EndsWith("combo-fr-de.state", combo.Depart!.State);
        Assert.Equal("fr", combo.Langue);
        // L'ordre des places compte.
        var autre = AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 2, "eu1", null, [J(1, "de_DE", true), J(2, "fr_FR")], null);
        Assert.EndsWith("eu-en-2.state", autre.Depart!.State);
        // Une langue inconnue n'est jamais devinee : la regle de la machine partagee (l'hote lit le francais).
        var inconnue = AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 2, "eu1", null, [J(1, "fr_FR", true), new AdventureJoueurLangue(2, false, [])], null);
        Assert.EndsWith("eu-fr-2.state", inconnue.Depart!.State);
        // Une aventure deja fixee dans une langue n'est pas rejouee en combinaison.
        var fixee = AdventureLangues.ChoisirLeDepart(version, entree.Jeu, 2, "eu1", null, [J(1, "fr_FR", true), J(2, "de_DE")], "en");
        Assert.EndsWith("eu-en-2.state", fixee.Depart!.State);
    }

    // ── la seance : l'hote choisit le depart selon la langue de son ES, et l'aventure la garde ─────

    private sealed class SiteMuet : IAdventureSite
    {
        public AdventureInfo? Aventure { get; set; }
        public Task<AdventureInfo?> CreerAsync(string jeu, string contenu, int places, string mode, string? titre, string? coeur, string? empreinteDuCoeur, CancellationToken ct, string? version = null) => Task.FromResult<AdventureInfo?>(null);
        public Task<AdventureMesAventures?> MesAventuresAsync(CancellationToken ct) => Task.FromResult<AdventureMesAventures?>(null);
        public Task<IReadOnlyList<AdventureInfo>?> OuvertesAsync(string? jeu, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdventureInfo>?>(null);
        public Task<AdventureInfo?> DetailAsync(string aventure, CancellationToken ct) => Task.FromResult(Aventure);
        public Task<AdventureDerniere?> DerniereAsync(string aventure, int? joueurs, CancellationToken ct) => Task.FromResult<AdventureDerniere?>(null);
        public Task<AdventureSeance?> OuvrirSeanceAsync(string aventure, int joueurs, CancellationToken ct) => Task.FromResult<AdventureSeance?>(null);
        public Task<bool> FermerSeanceAsync(string aventure, string raison, long? dureeS, CancellationToken ct) => Task.FromResult(true);
        public Task<(bool Ok, string? Raison)> InviterAsync(string aventure, string poignee, CancellationToken ct) => Task.FromResult<(bool, string?)>((false, null));
        public Task<bool> AccepterAsync(string invitation, bool accepter, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<string>?> ObjetsManquantsAsync(IReadOnlyCollection<string> empreintes, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<bool> DeposerObjetAsync(string empreinte, string genre, byte[] octets, CancellationToken ct) => Task.FromResult(false);
        public Task<AdventureDepot> DeposerVersionAsync(AdventureVersion version, CancellationToken ct) => Task.FromResult(AdventureDepot.Parti);
        public Task<byte[]?> TelechargerObjetAsync(string aventure, string empreinte, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }

    private static MultiplayerRoom Salle(params MultiplayerJoueur[] joueurs)
        => new()
        {
            Room = "r1", Game = "gba/jeu", Content = "eu1", Adventure = new MultiplayerAventure { Id = "av-1", Session = "s" }, State = "launching",
            Launch = new MultiplayerLancement { Role = "host", Seat = 1, Players = joueurs.Length }, Players = joueurs,
        };

    [Fact]
    public async Task L_hote_francais_part_en_francais_et_l_aventure_garde_cette_langue_a_la_reprise()
    {
        var chemin = EcrireUnCatalogue(JeuPartiel());
        var catalogue = new AdventureCatalogueStore(path: chemin);
        var stockage = new AdventureStockage(Path.Combine(_racine, "adventure"));
        var site = new SiteMuet { Aventure = new AdventureInfo { Id = "av-1", Game = "gba/jeu", Content = "eu1", Seats = 4 } };
        // La langue de la borne change entre deux seances : celle de l'aventure, fixee a la premiere, gagne.
        var langue = "fr_FR";
        var service = new AdventureSeanceService(site, stockage, new AdventureFileDEnvoi(stockage), () => new FauxCommandes(), _ => null, () => DateTime.UtcNow,
            catalogue: catalogue, langueDeLaBorne: () => langue);
        var jeu = new MultiplayerInstalledGame(Assert.Single(catalogue.Entrees(out _)).Jeu, Path.Combine(_racine, "jeu.gba"), new MultiplayerCore { Core = "mgba_splitscreen" }, "eu1");

        var salle = Salle(new MultiplayerJoueur { Seat = 1, Handle = "moi", You = true, Host = true }, new MultiplayerJoueur { Seat = 2, Handle = "ami", Language = "fr" });
        Assert.True(await service.PreparerAsync(salle, jeu, CancellationToken.None));
        Assert.EndsWith("eu-fr-2.state", service.DepartDe(salle)!.State);
        Assert.Equal("fr", stockage.Langue("av-1"));
        Assert.Equal("fr", service.De("r1")!.Langue);

        // Une autre seance, l'hote lit maintenant l'allemand : l'aventure reste en francais.
        langue = "de_DE";
        var autre = Salle(new MultiplayerJoueur { Seat = 1, Handle = "moi", You = true, Host = true }, new MultiplayerJoueur { Seat = 2, Handle = "ami", Language = "de" });
        Assert.True(await service.PreparerAsync(autre, jeu, CancellationToken.None));
        Assert.EndsWith("eu-fr-2.state", service.DepartDe(autre)!.State);
        // A 3 joueurs il n'y a pas de depart francais : a froid, jamais dans une autre langue.
        var trois = Salle(new MultiplayerJoueur { Seat = 1, Handle = "moi", You = true, Host = true }, new MultiplayerJoueur { Seat = 2, Handle = "a" }, new MultiplayerJoueur { Seat = 3, Handle = "b" });
        Assert.True(await service.PreparerAsync(trois, jeu, CancellationToken.None));
        Assert.Null(service.DepartDe(trois));
    }

    [Fact]
    public async Task Un_site_qui_garde_la_langue_de_l_aventure_passe_avant_la_copie_locale()
    {
        var catalogue = new AdventureCatalogueStore(path: EcrireUnCatalogue(JeuPartiel()));
        var stockage = new AdventureStockage(Path.Combine(_racine, "adventure"));
        stockage.FixerLaLangue("av-1", "fr");
        var site = new SiteMuet { Aventure = new AdventureInfo { Id = "av-1", Game = "gba/jeu", Content = "eu1", Seats = 4, Language = "en" } };
        var service = new AdventureSeanceService(site, stockage, new AdventureFileDEnvoi(stockage), () => new FauxCommandes(), _ => null, () => DateTime.UtcNow,
            catalogue: catalogue, langueDeLaBorne: () => "fr_FR");
        var jeu = new MultiplayerInstalledGame(Assert.Single(catalogue.Entrees(out _)).Jeu, Path.Combine(_racine, "jeu.gba"), new MultiplayerCore { Core = "mgba_splitscreen" }, "eu1");
        var salle = Salle(new MultiplayerJoueur { Seat = 1, Handle = "moi", You = true, Host = true }, new MultiplayerJoueur { Seat = 2, Handle = "ami" });
        Assert.True(await service.PreparerAsync(salle, jeu, CancellationToken.None));
        Assert.EndsWith("eu-en-2.state", service.DepartDe(salle)!.State);
        Assert.Equal("fr", stockage.Langue("av-1"));   // jamais remplacee
    }

    // ── la collection d'ES ne montre que la langue de la borne ─────────────────────────────────────

    private sealed class SansDefinition : RetroBat.Api.Media.IScoreSlugResolver
    {
        public string? ResolveScoreSlug(string systemId, string? romFileName, string? md5, string? cheevosHash) => null;
    }

    private sealed class FauxOptions(ApiExposeOptions valeur) : Microsoft.Extensions.Options.IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; } = valeur;
        public ApiExposeOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<ApiExposeOptions, string?> listener) => new Rien();
        private sealed class Rien : IDisposable { public void Dispose() { } }
    }

    private sealed class FauxReglages : RetroBat.Domain.Interfaces.IEsSettingsStore
    {
        private readonly System.Xml.Linq.XDocument _document = new(new System.Xml.Linq.XElement("config"));
        public bool Update(Func<System.Xml.Linq.XDocument, bool> update, CancellationToken cancellationToken = default) => update(_document);
        public IReadOnlyDictionary<string, string> ReadAllSettings() => new Dictionary<string, string>();
        public RetroBat.Domain.Interfaces.EmulationStationSettingsSnapshot ReadSnapshot() => new(string.Empty, DateTime.UtcNow, ReadAllSettings());
        public void Invalidate() { }
        public Task WaitForStableFileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static object JeuAVersions() => new
    {
        id = "gba/jeu", system = "gba", name = "Jeu", family = "adventure", players = new { min = 2, max = 4 }, core = "mgba_splitscreen",
        content = new { md5 = new[] { "m-usa" } },
        default_version = "usa",
        versions = new object[]
        {
            new { key = "usa", languages = new[] { "en" }, content = new { md5 = new[] { "m-usa" }, size = new[] { 8L } } },
            new { key = "europe", languages = new[] { "en", "de" }, start_language = "en", content = new { md5 = new[] { "m-eu" }, size = new[] { 8L } },
                  language_starts = new Dictionary<string, object> { ["fr"] = new { } } },
            new { key = "japan", languages = new[] { "ja" }, content = new { md5 = new[] { "m-jp" }, size = new[] { 8L } } },
            new { key = "germany", languages = new[] { "de" }, content = new { md5 = new[] { "m-de" }, size = new[] { 8L } } },
        },
    };

    private (MultiplayerCollectionSyncService Service, AdventureCatalogueStore Store, string Langue) UneCollection(string[] roms, Func<string?> langue)
    {
        var dossier = Path.Combine(_racine, "roms", "gba");
        Directory.CreateDirectory(dossier);
        foreach (var rom in roms)
        {
            File.WriteAllText(Path.Combine(dossier, rom + ".gba"), rom.PadRight(8, '.'));   // 8 octets, comme le catalogue
        }

        var store = new AdventureCatalogueStore(path: EcrireUnCatalogue(JeuAVersions(), "langues.json"));
        var etat = Path.Combine(_racine, "state");
        var service = new MultiplayerCollectionSyncService(
            new FauxOptions(new ApiExposeOptions { NelfePlay = new NelfePlayOptions { ShowAdventureCollection = true } }),
            store.Interne,
            new InstalledGameCatalog(new SansDefinition(), logger: null, romsRoot: Path.Combine(_racine, "roms"), ramRoot: Path.Combine(_racine, "ram")),
            new EsCustomCollectionWriter(new FauxReglages(), logger: null, collectionsRoot: Path.Combine(_racine, "collections"), stateRoot: etat),
            new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"), sourceRoot: Path.Combine(_racine, "assets"), stateRoot: etat),
            new RetroBat.Domain.Models.MediaRuntimeState(),
            romsRoot: Path.Combine(_racine, "roms"),
            emulateurTourne: () => false,
            md5DuContenu: chemin => "m-" + File.ReadAllText(chemin).TrimEnd('.'),
            coeurPose: _ => true,
            profil: AdventureCollection.ProfilDeLaBorne(store, langue));
        return (service, store, string.Empty);
    }

    private static string[] Montrees(MultiplayerCollectionSyncService s) => s.JeuxConnus().Select(j => j.ContentKey).Order().ToArray();

    private string[] DansEs(MultiplayerCollectionSyncService s)
    {
        var fichier = Directory.GetFiles(Path.Combine(_racine, "collections"), "*adventure*", SearchOption.AllDirectories).FirstOrDefault();
        return fichier is null ? [] : File.ReadAllLines(fichier).Where(l => l.Contains(".gba", StringComparison.Ordinal))
            .Select(l => Path.GetFileNameWithoutExtension(l.Trim().Replace("</path>", "").Replace("<path>", ""))).Order().ToArray();
    }

    [Theory]
    [InlineData("fr_FR", new[] { "usa", "eu" }, new[] { "eu" })]            // fr : l'Europe (language_starts fr), pas l'USA
    [InlineData("de_DE", new[] { "usa", "eu", "de" }, new[] { "de", "eu" })] // de : Europe et Allemagne
    [InlineData("en_US", new[] { "usa", "eu", "jp" }, new[] { "eu", "usa" })]
    [InlineData("es_ES", new[] { "usa", "eu", "jp" }, new[] { "eu", "usa" })]  // pas de version espagnole : l'anglais
    [InlineData("es_ES", new[] { "jp", "de" }, new[] { "jp" })]               // ni espagnole ni anglaise : la version par defaut ? absente ici
    public void La_collection_ne_montre_que_les_versions_de_la_langue_de_la_borne(string langue, string[] roms, string[] attendu)
    {
        var (service, _, _) = UneCollection(roms, () => langue);
        service.SynchroniserAsync("test", CancellationToken.None).GetAwaiter().GetResult();
        var visibles = DansEs(service);
        // Le cas « ni la langue, ni l'anglais, ni la version par defaut » montre TOUTES les versions possedees (jp, de).
        var voulu = langue == "es_ES" && roms.SequenceEqual(["jp", "de"]) ? new[] { "de", "jp" } : attendu;
        Assert.Equal(voulu, visibles.Select(v => v.Replace(" ", "")).Select(Cle).Order().ToArray());
    }

    private static string Cle(string nom) => nom switch { "usa" => "usa", "eu" => "eu", "jp" => "jp", "de" => "de", _ => nom };

    [Fact]
    public void Une_seule_rom_dans_une_autre_langue_s_affiche_quand_meme()
    {
        var (service, _, _) = UneCollection(["jp"], () => "fr_FR");
        service.SynchroniserAsync("test", CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(["jp"], DansEs(service));
        Assert.Equal(["m-jp"], Montrees(service));
    }

    [Fact]
    public void La_version_par_defaut_passe_avant_les_autres_quand_ni_la_langue_ni_l_anglais_ne_conviennent_pas()
    {
        // Borne en francais, versions japonaise et allemande possedees : ni fr ni en, et la version par defaut (usa) n'est pas possedee : tout.
        var (service, _, _) = UneCollection(["jp", "de"], () => "fr_FR");
        service.SynchroniserAsync("test", CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(["de", "jp"], DansEs(service));
    }

    [Fact]
    public void Une_version_cachee_reste_connue_de_la_borne_pour_une_invitation_un_join_ou_une_reprise()
    {
        var (service, store, _) = UneCollection(["usa", "eu"], () => "fr_FR");
        service.SynchroniserAsync("test", CancellationToken.None).GetAwaiter().GetResult();
        // La collection d'ES ne montre que l'Europe...
        Assert.Equal(["eu"], DansEs(service));
        // ...mais la borne possede toujours les deux : l'invite qui a la ROM USA de l'hote la retrouve par jeu ET contenu.
        Assert.Equal(["m-eu", "m-usa"], Montrees(service));
        var usa = service.JeuxConnus().Single(j => j.ContentKey == "m-usa");
        Assert.Equal("gba/jeu", usa.Game.Id);
        Assert.Contains(usa.Path, service.JeuxConnus().Select(j => j.Path));
        // La reprise d'une aventure creee sur l'USA : la version est celle du contenu de l'aventure, pas celle de la collection.
        Assert.Equal("usa", store.Entrees(out _).Single().Spec.VersionDuContenu(usa.ContentKey)?.Key);
    }

    [Fact]
    public void Les_langues_d_une_version_sont_celles_du_catalogue_languages_start_language_et_language_starts()
    {
        var store = new AdventureCatalogueStore(path: EcrireUnCatalogue(JeuAVersions(), "l.json"));
        var europe = store.Entrees(out _).Single().Spec.Versions.Single(v => v.Key == "europe");
        Assert.Equal(["en", "de", "fr"], europe.Langues());
    }

    [Fact]
    public void Une_aventure_creee_sur_une_version_cachee_par_le_filtre_de_langue_se_reprend_quand_la_borne_la_possede()
    {
        var miennes = new[]
        {
            new AdventureInfo { Id = "a-usa", Game = "gba/jeu", Content = "m-usa" },
            new AdventureInfo { Id = "a-x", Game = "gba/autre", Content = "m-usa" },
        };
        // Sans filtre : aucune aventure sur la version montree (europe).
        Assert.Null(AdventureChoix.Choisir(miennes, "gba/jeu", "m-eu"));
        // Avec le contenu possede (version USA cachee) : on reprend l'aventure USA.
        Assert.Equal("a-usa", AdventureChoix.Choisir(miennes, "gba/jeu", "m-eu", (jeu, contenu) => contenu == "m-usa")!.Id);
        // Une aventure sur un contenu que la borne n'a pas : jamais reprise (le netplay se desynchroniserait).
        Assert.Null(AdventureChoix.Choisir(miennes, "gba/jeu", "m-eu", (jeu, contenu) => false));
        // La version montree avec sa propre aventure passe avant.
        var deux = miennes.Append(new AdventureInfo { Id = "a-eu", Game = "gba/jeu", Content = "m-eu" }).ToList();
        Assert.Equal("a-eu", AdventureChoix.Choisir(deux, "gba/jeu", "m-eu", (jeu, contenu) => true)!.Id);
    }

    [Fact]
    public void Les_panneaux_en_jeu_prennent_le_logo_d_Adventure_et_se_replient_sur_celui_de_MULTIPLAYER_s_il_manque()
    {
        var bandes = RetroBat.Api.Leaderboard.PanneauxEnJeu.Calculer(1920, 1080, 4.0 / 3.0)!;
        foreach (var logo in new string?[] { null, AdventureCollection.Logo, "absent.png" })
        {
            using var image = RetroBat.Api.Leaderboard.PanneauxEnJeu.Dessiner(1920, 1080, bandes, "coop", null,
                [new RetroBat.Api.Leaderboard.PanneauxEnJeu.Joueur(1, "P1", "", null, "", "", null, null)], fichierDuLogo: logo);
            Assert.Equal(1920, image.Width);
        }
    }

    private sealed class FauxCommandes : ICommandesRetroArch
    {
        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500) => Task.FromResult<string?>(null);
        public Task EnvoyerAsync(string commande, CancellationToken ct) => Task.CompletedTask;
    }
}
