using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La collection « World Scoring » bout en bout, sans reseau : ce qui entre, ce qui reste
/// dehors, et ce qui survit a une panne. Une panne prise pour une liste vide effacerait la
/// collection du joueur ; une definition non homologuee acceptee lui promettrait un score
/// refuse a la fin de la partie.
/// </summary>
public class NelfePlayScoringCollectionSyncTests : IDisposable
{
    private const string MemContenu = "SCORE_STATE 0x1234";
    private static readonly string MemEmpreinte =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MemContenu))).ToLowerInvariant();

    private readonly string _racine = Path.Combine(Path.GetTempPath(), "world-scoring-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FauxReglages _reglages = new();
    private readonly NelfePlayOptions _nelfeplay = new();

    private string Roms => Path.Combine(_racine, "roms");

    private string Ram => Path.Combine(_racine, "ram");

    private string Etat => Path.Combine(_racine, "state");

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── montage ──────────────────────────────────────────────────────────────

    private EsCustomCollectionWriter Writer() => new(
        _reglages, logger: null,
        collectionsRoot: Path.Combine(_racine, "collections"),
        stateRoot: Etat);

    private NelfePlayScoringCollectionSyncService Service(FauxHttp http, FauxResolveur resolveur)
        => new(
            http,
            new FauxOptions(new ApiExposeOptions { NelfePlay = _nelfeplay }),
            new InstalledGameCatalog(resolveur, logger: null, romsRoot: Roms, ramRoot: Ram),
            Writer(),
            new EsCollectionThemeAssets(logger: null, themesRoot: Path.Combine(_racine, "themes"),
                sourceRoot: Path.Combine(_racine, "assets"), stateRoot: Etat),
            new MediaRuntimeState(),
            logger: null,
            stateRoot: Etat,
            minimumEntreDeuxAppels: TimeSpan.Zero);

    private string PoserRom(string systeme, string nom)
    {
        var dossier = Path.Combine(Roms, systeme);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nom);
        File.WriteAllText(chemin, "rom");
        return chemin;
    }

    private void PoserMem(string systemeCanonique, string slug, string contenu)
    {
        var dossier = Path.Combine(Ram, systemeCanonique);
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, slug + ".MEM"), contenu);
    }

    private static string Index(string revision, params string[] jeux)
        => $"{{\"ok\":true,\"schema_version\":1,\"generated_at\":\"2026-09-18T08:00:00Z\",\"revision\":\"{revision}\",\"games\":[{string.Join(",", jeux)}]}}";

    private static string Jeu(string systeme, string groupe, string mem, string? md5 = null)
        => $"{{\"system_id\":\"{systeme}\",\"rom_group\":\"{groupe}\",\"mem_sha256\":\"{mem}\"," +
           $"\"content_hashes\":{{\"md5\":[{(md5 == null ? string.Empty : $"\"{md5}\"")}],\"sha1\":[],\"sha256\":[]}}," +
           "\"rulesets\":[{\"id\":\"1cc\",\"profile_version\":1}]}";

    private string[] Collection()
    {
        var chemin = Writer().ConfigPath(NelfePlayScoringCollectionSyncService.CollectionName);
        return File.Exists(chemin) ? File.ReadAllLines(chemin) : [];
    }

    // ── cas nominal ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_jeu_possede_et_homologue_entre_dans_la_collection()
    {
        var rom = PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));
        var service = Service(http, new FauxResolveur { ["19xx.zip"] = "19xx" });

        var statut = await service.SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("ready", statut.State);
        Assert.Equal(1, statut.LocalReadyGames);
        Assert.Equal(1, statut.RemoteGames);
        Assert.False(statut.Stale);
        Assert.Equal(new[] { rom.Replace('\\', '/') }, Collection());
        Assert.Equal("nelfeplay-scoring", _reglages.Valeur("CollectionSystemsCustom"));
    }

    [Fact]
    public async Task Un_jeu_ouvert_mais_absent_de_la_machine_n_apparait_pas()
    {
        PoserMem("arcade", "19xx", MemContenu);
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));

        var statut = await Service(http, new FauxResolveur()).SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("empty", statut.State);
        Assert.Empty(Collection());
    }

    [Fact]
    public async Task Une_definition_locale_non_homologuee_exclut_le_jeu()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", "UNE AUTRE DEFINITION");
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));

        var statut = await Service(http, new FauxResolveur { ["19xx.zip"] = "19xx" })
            .SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("empty", statut.State);
        Assert.Empty(Collection());
    }

    [Fact]
    public async Task Une_definition_locale_absente_exclut_le_jeu()
    {
        PoserRom("fbneo", "19xx.zip");
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));

        var statut = await Service(http, new FauxResolveur { ["19xx.zip"] = "19xx" })
            .SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("empty", statut.State);
    }

    [Fact]
    public async Task Rien_de_ce_que_la_borne_possede_ne_part_sur_le_reseau()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));

        await Service(http, new FauxResolveur { ["19xx.zip"] = "19xx" }).SynchroniserAsync("test", CancellationToken.None);

        Assert.All(http.Requetes, requete =>
        {
            Assert.Equal(HttpMethod.Get, requete.Method);
            Assert.Null(requete.Content);
            Assert.Equal("/api/v1/scores/open-games", requete.RequestUri?.AbsolutePath);
        });
    }

    // ── resilience ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Une_panne_reseau_conserve_la_derniere_collection()
    {
        var rom = PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));
        await Service(http, resolveur).SynchroniserAsync("test", CancellationToken.None);

        var panne = new FauxHttp(string.Empty, HttpStatusCode.InternalServerError);
        var statut = await Service(panne, resolveur).SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("stale", statut.State);
        Assert.True(statut.Stale);
        Assert.Equal("HTTP 500", statut.LastError);
        Assert.Equal(new[] { rom.Replace('\\', '/') }, Collection());
    }

    [Fact]
    public async Task Une_reponse_invalide_n_est_jamais_prise_pour_une_liste_vide()
    {
        var rom = PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };
        await Service(new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte))), resolveur)
            .SynchroniserAsync("test", CancellationToken.None);

        var statut = await Service(new FauxHttp("{\"ok\":false}"), resolveur)
            .SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("stale", statut.State);
        Assert.Equal(new[] { rom.Replace('\\', '/') }, Collection());
    }

    [Fact]
    public async Task Une_liste_vide_valide_vide_et_masque_la_collection()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };
        await Service(new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte))), resolveur)
            .SynchroniserAsync("test", CancellationToken.None);

        var statut = await Service(new FauxHttp(Index("sha256:bb")), resolveur)
            .SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("empty", statut.State);
        Assert.False(statut.Stale);
        Assert.Empty(Collection());
        Assert.Null(_reglages.Valeur("CollectionSystemsCustom"));
    }

    [Fact]
    public async Task Sur_304_l_index_garde_en_local_rejoue_l_intersection()
    {
        var rom = PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };
        var premier = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)), etag: "\"sha256:aa\"");
        await Service(premier, resolveur).SynchroniserAsync("test", CancellationToken.None);
        File.Delete(Writer().ConfigPath(NelfePlayScoringCollectionSyncService.CollectionName));

        var second = new FauxHttp(string.Empty, HttpStatusCode.NotModified);
        var statut = await Service(second, resolveur).SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("ready", statut.State);
        Assert.Equal(new[] { rom.Replace('\\', '/') }, Collection());
        Assert.Equal("\"sha256:aa\"", second.Requetes[0].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task Sans_index_connu_un_304_ne_fabrique_pas_de_collection()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);

        var statut = await Service(new FauxHttp(string.Empty, HttpStatusCode.NotModified),
                new FauxResolveur { ["19xx.zip"] = "19xx" })
            .SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("error", statut.State);
        Assert.Empty(Collection());
    }

    [Fact]
    public async Task Deux_passes_identiques_ne_reecrivent_rien()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };
        var corps = Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte));
        var service = Service(new FauxHttp(corps), resolveur);
        await service.SynchroniserAsync("test", CancellationToken.None);
        var chemin = Writer().ConfigPath(NelfePlayScoringCollectionSyncService.CollectionName);
        var ecritAvant = File.GetLastWriteTimeUtc(chemin);

        await Service(new FauxHttp(corps), resolveur).SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal(ecritAvant, File.GetLastWriteTimeUtc(chemin));
    }

    // ── options ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task L_option_coupee_retire_la_collection()
    {
        var rom = PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var resolveur = new FauxResolveur { ["19xx.zip"] = "19xx" };
        await Service(new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte))), resolveur)
            .SynchroniserAsync("test", CancellationToken.None);
        Assert.NotEmpty(Collection());

        _nelfeplay.ShowScoringCollection = false;
        var statut = await Service(new FauxHttp(string.Empty), resolveur).SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("disabled", statut.State);
        Assert.Empty(Collection());
        Assert.Null(_reglages.Valeur("CollectionSystemsCustom"));
        Assert.True(File.Exists(rom), "la ROM du joueur n'est jamais touchee");
    }

    [Fact]
    public async Task Le_bouton_maitre_coupe_aussi_la_synchronisation()
    {
        _nelfeplay.Enabled = false;
        var http = new FauxHttp(Index("sha256:aa"));

        var statut = await Service(http, new FauxResolveur()).SynchroniserAsync("test", CancellationToken.None);

        Assert.Equal("disabled", statut.State);
        Assert.Empty(http.Requetes);
    }

    // ── choix d'une variante ─────────────────────────────────────────────────

    [Fact]
    public void La_variante_reconnue_par_le_profil_est_preferee()
    {
        var jeu = new OpenGame
        {
            SystemId = "megadrive",
            RomGroup = "sonic-the-hedgehog",
            MemSha256 = MemEmpreinte,
            ContentHashes = new ContentHashes { Md5 = ["bbbb"] },
        };
        var candidats = new List<InstalledGame>
        {
            Installe("E:/roms/megadrive/Sonic (Japan).zip", "aaaa"),
            Installe("E:/roms/megadrive/Sonic (Europe).zip", "bbbb"),
        };

        Assert.Equal("E:/roms/megadrive/Sonic (Europe).zip",
            NelfePlayScoringCollectionSyncService.Choisir(candidats, jeu, []));
    }

    [Fact]
    public void Le_chemin_deja_choisi_reste_choisi()
    {
        var jeu = new OpenGame
        {
            SystemId = "megadrive",
            RomGroup = "sonic-the-hedgehog",
            MemSha256 = MemEmpreinte,
            ContentHashes = new ContentHashes { Md5 = ["bbbb"] },
        };
        var candidats = new List<InstalledGame>
        {
            Installe("E:/roms/megadrive/Sonic (Japan).zip", "aaaa"),
            Installe("E:/roms/megadrive/Sonic (Europe).zip", "bbbb"),
        };
        var precedent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "E:/roms/megadrive/Sonic (Japan).zip" };

        Assert.Equal("E:/roms/megadrive/Sonic (Japan).zip",
            NelfePlayScoringCollectionSyncService.Choisir(candidats, jeu, precedent));
    }

    [Fact]
    public void Sans_hash_connu_le_nom_du_groupe_puis_l_ordre_tranchent()
    {
        var jeu = new OpenGame { SystemId = "arcade", RomGroup = "19xx", MemSha256 = MemEmpreinte };
        var candidats = new List<InstalledGame>
        {
            Installe("E:/roms/fbneo/19xxa.zip", null),
            Installe("E:/roms/fbneo/19xx.zip", null),
        };

        Assert.Equal("E:/roms/fbneo/19xx.zip",
            NelfePlayScoringCollectionSyncService.Choisir(candidats, jeu, []));
    }

    // ── le panneau ES ne s'ouvre que sur un jeu ouvert ────────────────────

    [Fact]
    public async Task Un_jeu_de_la_collection_est_ouvert_au_scoring()
    {
        var rom = PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));
        var service = Service(http, new FauxResolveur { ["19xx.zip"] = "19xx" });
        await service.SynchroniserAsync("test", CancellationToken.None);

        Assert.True(service.EstOuvertAuScoring(rom));
        // Le separateur du chemin ne doit pas changer la reponse : ES en donne un, la
        // collection en ecrit un autre.
        Assert.True(service.EstOuvertAuScoring(rom.Replace('/', '\\')));
        Assert.False(service.EstOuvertAuScoring(Path.Combine(Roms, "fbneo", "autre.zip")));
    }

    [Fact]
    public async Task Sans_aucune_liste_on_ne_restreint_rien()
    {
        // Ne pas savoir n'est pas savoir que non : un panneau qui ne s'ouvre nulle part apres
        // un demarrage hors ligne serait pris pour une panne.
        PoserMem("arcade", "19xx", MemContenu);
        var http = new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)));
        var service = Service(http, new FauxResolveur());
        await service.SynchroniserAsync("test", CancellationToken.None);

        Assert.Null(service.EstOuvertAuScoring(Path.Combine(Roms, "fbneo", "19xx.zip")));
    }

    [Fact]
    public void Un_chemin_vide_ne_dit_rien()
    {
        var service = Service(new FauxHttp(Index("sha256:aa")), new FauxResolveur());
        Assert.Null(service.EstOuvertAuScoring(""));
    }
    // ── priorite de lancement : ce qui se rejoue passe devant ────────────────

    [Fact]
    public void Le_dump_qui_se_lance_sous_FBNeo_passe_devant_MAME_autonome()
    {
        var jeu = new OpenGame { SystemId = "arcade", RomGroup = "double-dragon", MemSha256 = MemEmpreinte };
        var candidats = new List<InstalledGame>
        {
            InstalleArcade("mame", "E:/roms/mame/ddragon.zip"),
            InstalleArcade("fbneo", "E:/roms/fbneo/ddragon.zip"),
        };

        Assert.Equal("E:/roms/fbneo/ddragon.zip", NelfePlayScoringCollectionSyncService.Choisir(
            candidats, jeu, [], Lance(("fbneo", "libretro", "fbneo"), ("mame", "mame64", ""))));
    }

    [Fact]
    public void Entre_deux_MAME_celui_de_RetroArch_est_pris()
    {
        // Sans FBNeo installe, MAME sous RetroArch reste preferable au binaire autonome :
        // lui seul rend un replay.
        var jeu = new OpenGame { SystemId = "arcade", RomGroup = "double-dragon", MemSha256 = MemEmpreinte };
        var candidats = new List<InstalledGame>
        {
            InstalleArcade("mame-seul", "E:/roms/mame-seul/ddragon.zip"),
            InstalleArcade("mame", "E:/roms/mame/ddragon.zip"),
        };

        Assert.Equal("E:/roms/mame/ddragon.zip", NelfePlayScoringCollectionSyncService.Choisir(
            candidats, jeu, [], Lance(("mame", "libretro", "mame"), ("mame-seul", "mame64", ""))));
    }

    [Fact]
    public void La_priorite_deplace_un_choix_deja_pose()
    {
        // Une borne epinglee sur MAME autonome ne doit pas y rester : le replay manquerait a
        // chacun de ses records.
        var jeu = new OpenGame { SystemId = "arcade", RomGroup = "double-dragon", MemSha256 = MemEmpreinte };
        var candidats = new List<InstalledGame>
        {
            InstalleArcade("mame", "E:/roms/mame/ddragon.zip"),
            InstalleArcade("fbneo", "E:/roms/fbneo/ddragon.zip"),
        };
        var precedent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "E:/roms/mame/ddragon.zip" };

        Assert.Equal("E:/roms/fbneo/ddragon.zip", NelfePlayScoringCollectionSyncService.Choisir(
            candidats, jeu, precedent, Lance(("fbneo", "libretro", "fbneo"), ("mame", "mame64", ""))));
    }

    [Fact]
    public void A_rang_egal_le_choix_deja_pose_reste()
    {
        var jeu = new OpenGame { SystemId = "arcade", RomGroup = "double-dragon", MemSha256 = MemEmpreinte };
        var candidats = new List<InstalledGame>
        {
            InstalleArcade("fbneo", "E:/roms/fbneo/ddragona.zip"),
            InstalleArcade("fbneo", "E:/roms/fbneo/ddragon.zip"),
        };
        var precedent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "E:/roms/fbneo/ddragona.zip" };

        Assert.Equal("E:/roms/fbneo/ddragona.zip", NelfePlayScoringCollectionSyncService.Choisir(
            candidats, jeu, precedent, Lance(("fbneo", "libretro", "fbneo"))));
    }

    [Theory]
    [InlineData("libretro", "fbneo", 0)]
    [InlineData("libretro", "fbalpha2012_cps2", 0)]
    [InlineData("libretro", "mame", 1)]
    // UN COEUR DE CONSOLE N'EST PAS PENALISE : la regle vise le replay, pas FBNeo en soi.
    // Genesis Plus GX enregistre aussi bien, il vaut donc le rang d'un coeur libretro ordinaire.
    [InlineData("libretro", "genesis_plus_gx", 1)]
    [InlineData("mame64", "", 2)]
    [InlineData("raine", "", 2)]
    public void Le_rang_de_lancement_dit_ce_qui_se_rejoue(string emulateur, string coeur, int attendu)
    {
        Assert.Equal(attendu, NelfePlayScoringCollectionSyncService.RangDeLancement(
            new EmulationStationLaunchConfig("arcade", emulateur, coeur)));
    }

    [Fact]
    public void Sans_configuration_lisible_aucun_dump_n_est_favorise()
    {
        // Rang neutre : le tri d'avant (hash reconnu, puis nom) tranche seul.
        Assert.Equal(1, NelfePlayScoringCollectionSyncService.RangDeLancement(null));
    }

    // ── contrat de l'index ───────────────────────────────────────────────────

    [Fact]
    public void Un_index_hors_contrat_est_refuse()
    {
        Assert.NotNull(NelfePlayScoringCollectionSyncService.Valider(null));
        Assert.NotNull(NelfePlayScoringCollectionSyncService.Valider(new OpenGamesManifest { Ok = false }));
        Assert.NotNull(NelfePlayScoringCollectionSyncService.Valider(
            new OpenGamesManifest { Ok = true, SchemaVersion = 2, Revision = "sha256:aa" }));
        Assert.NotNull(NelfePlayScoringCollectionSyncService.Valider(
            new OpenGamesManifest { Ok = true, SchemaVersion = 1, Revision = "" }));
        Assert.Null(NelfePlayScoringCollectionSyncService.Valider(
            new OpenGamesManifest { Ok = true, SchemaVersion = 1, Revision = "sha256:aa", Games = [] }));
    }

    [Fact]
    public void Les_entrees_incompletes_sont_ecartees_et_les_doublons_fusionnes()
    {
        var manifeste = new OpenGamesManifest
        {
            Ok = true,
            SchemaVersion = 1,
            Revision = "sha256:aa",
            Games =
            [
                new OpenGame { SystemId = "ARCADE", RomGroup = "19XX", MemSha256 = MemEmpreinte.ToUpperInvariant() },
                new OpenGame { SystemId = "arcade", RomGroup = "19xx", MemSha256 = MemEmpreinte },
                new OpenGame { SystemId = "arcade", RomGroup = "sans-mem" },
                new OpenGame { SystemId = "", RomGroup = "19xx", MemSha256 = MemEmpreinte },
                new OpenGame { SystemId = "nes", RomGroup = "contra", MemSha256 = "pas-une-empreinte" },
            ],
        };

        var propre = NelfePlayScoringCollectionSyncService.Nettoyer(manifeste);

        Assert.Single(propre.Games);
        Assert.Equal("arcade", propre.Games[0].SystemId);
        Assert.Equal("19xx", propre.Games[0].RomGroup);
        Assert.Equal(MemEmpreinte, propre.Games[0].MemSha256);
    }

    [Fact]
    public async Task L_etat_sur_disque_permet_le_diagnostic()
    {
        PoserRom("fbneo", "19xx.zip");
        PoserMem("arcade", "19xx", MemContenu);
        await Service(new FauxHttp(Index("sha256:aa", Jeu("arcade", "19xx", MemEmpreinte)), etag: "\"sha256:aa\""),
                new FauxResolveur { ["19xx.zip"] = "19xx" })
            .SynchroniserAsync("test", CancellationToken.None);

        using var etat = JsonDocument.Parse(File.ReadAllText(Path.Combine(Etat, "scoring-collection.json")));
        var racine = etat.RootElement;
        Assert.Equal(1, racine.GetProperty("schema_version").GetInt32());
        Assert.Equal("sha256:aa", racine.GetProperty("remote_revision").GetString());
        Assert.Equal("\"sha256:aa\"", racine.GetProperty("remote_etag").GetString());
        Assert.Equal("custom-nelfeplay-scoring.cfg", racine.GetProperty("managed_file").GetString());
        Assert.Equal(1, racine.GetProperty("local_ready_games").GetInt32());
        Assert.Equal(1, racine.GetProperty("remote_games").GetInt32());
    }

    private static InstalledGame Installe(string chemin, string? md5) => new(
        "megadrive", "megadrive", "sonic-the-hedgehog", chemin,
        Path.GetFileNameWithoutExtension(chemin), md5, null, true, null);

    /// <summary>Le meme jeu d'arcade, range sous un systeme d'EmulationStation donne.</summary>
    private static InstalledGame InstalleArcade(string systemeFrontal, string chemin) => new(
        systemeFrontal, "arcade", "double-dragon", chemin,
        Path.GetFileNameWithoutExtension(chemin), null, null, true, null);

    /// <summary>Ce que la borne lancerait : « fbneo » sous RetroArch, « mame » standalone…</summary>
    private static Func<string, EmulationStationLaunchConfig?> Lance(
        params (string Systeme, string Emulateur, string Coeur)[] regles)
        => systeme =>
        {
            foreach (var (nom, emulateur, coeur) in regles)
            {
                if (string.Equals(nom, systeme, StringComparison.OrdinalIgnoreCase))
                {
                    return new EmulationStationLaunchConfig(systeme, emulateur, coeur);
                }
            }

            return null;
        };

    // ── doublures ────────────────────────────────────────────────────────────

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

    /// <summary>NelfePlay en bouteille : un corps, un code, un ETag, et la trace des appels.</summary>
    private sealed class FauxHttp(string corps, HttpStatusCode code = HttpStatusCode.OK, string? etag = null)
        : IHttpClientFactory
    {
        public List<HttpRequestMessage> Requetes { get; } = [];

        public HttpClient CreateClient(string name)
            => new(new Handler(this, corps, code, etag)) { BaseAddress = new Uri("https://nelfeplay.test") };

        private sealed class Handler(FauxHttp parent, string corps, HttpStatusCode code, string? etag) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                parent.Requetes.Add(request);
                var reponse = new HttpResponseMessage(code)
                {
                    Content = new StringContent(corps, Encoding.UTF8, "application/json"),
                };
                if (etag is { Length: > 0 })
                {
                    reponse.Headers.TryAddWithoutValidation("ETag", etag);
                }

                return Task.FromResult(reponse);
            }
        }
    }

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
