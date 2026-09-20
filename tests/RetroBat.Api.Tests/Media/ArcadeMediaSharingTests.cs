using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Un jeu d'arcade est un jeu d'arcade : le meme dump vit sous plusieurs dossiers roms et ses
/// medias sont les memes. Le store canonique suivait l'identifiant de systeme de ScreenScraper
/// (75 pour arcade, mame, fbneo, fba, hbmame ; 142 pour neogeo ; 7 pour cps2), donc le meme
/// jeu etait retelecharge d'un dossier a l'autre.
///
/// Mesure du 2026-09-19 sur les index de reference, pas sur une borne : 100 % des groupes de
/// cps1/cps2/cps3 et 94,8 % de ceux de neogeo sont deja dans arcade_lt.json. Et PIEGE de la
/// donnee : les empreintes ne peuvent pas servir de cle, arcade_lt.json n'a aucun md5, 76,9 %
/// de ses entrees n'ont aucune empreinte, et son crc 5a86cff2 est porte par 286 jeux (le BIOS
/// neogeo.zip). La cle est donc le slug du jeu.
/// </summary>
public class ArcadeMediaSharingTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "arcade-share-" + Guid.NewGuid().ToString("N")[..8]);

    public ArcadeMediaSharingTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Rom(string systeme, string nomFichier)
    {
        var dossier = Path.Combine(_racine, systeme);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nomFichier);
        File.WriteAllText(chemin, "rom");
        return chemin;
    }

    private static MediaProjectionPlan Plan(string frontend, string gamePath, params MediaNeed[] besoins) => new()
    {
        SystemId = "arcade",
        FrontendSystemId = frontend,
        GameSlug = "mslug",
        TextSourceGameSlug = "mslug",
        DisplayName = "Metal Slug - Super Vehicle-001",
        GamePath = gamePath,
        ProjectionBaseName = "mslug",
        IsArcadeLike = true,
        EsGameId = "source-gameid",
        RomRegions = ["wor"],
        RomLanguages = ["en"],
        Needs = besoins.ToList(),
    };

    private static GamelistUpdateService.GamelistEntryIdentity Identite(
        string gameId = "",
        string nom = "",
        string[]? regions = null,
        string[]? langues = null)
        => new(gameId, nom, regions ?? [], langues ?? []);

    // ── quels systemes recoivent l'entree ───────────────────────────────────────────────

    [Fact]
    public void Le_meme_dump_sous_un_autre_dossier_arcade_est_une_cible()
    {
        Rom("fbneo", "mslug.zip");
        var attendu = Rom("neogeo", "mslug.zip");

        var cibles = ArcadeMediaSharingService.CiblesPourRom(
            _racine,
            "fbneo",
            "mslug.zip",
            ["fbneo", "neogeo", "mame"]);

        Assert.Single(cibles);
        Assert.Equal("neogeo", cibles[0].FrontendSystemId);
        Assert.Equal(attendu, cibles[0].GamePath);
    }

    [Fact]
    public void Le_systeme_d_origine_n_est_jamais_sa_propre_cible()
    {
        Rom("fbneo", "mslug.zip");

        var cibles = ArcadeMediaSharingService.CiblesPourRom(_racine, "fbneo", "mslug.zip", ["fbneo"]);

        Assert.Empty(cibles);
    }

    [Fact]
    public void Un_dossier_qui_n_a_pas_ce_jeu_n_est_pas_une_cible()
    {
        Rom("fbneo", "mslug.zip");
        Rom("neogeo", "2020bb.zip");

        var cibles = ArcadeMediaSharingService.CiblesPourRom(_racine, "fbneo", "mslug.zip", ["neogeo", "cps2"]);

        Assert.Empty(cibles);
    }

    [Fact]
    public void Un_systeme_annonce_deux_fois_ne_donne_qu_une_cible()
    {
        Rom("neogeo", "mslug.zip");

        var cibles = ArcadeMediaSharingService.CiblesPourRom(_racine, "mame", "mslug.zip", ["neogeo", "neogeo", "NEOGEO"]);

        Assert.Single(cibles);
    }

    // ── l'entree portee vers le systeme frere ───────────────────────────────────────────

    /// <summary>
    /// La regle du projet : on n'ecrit jamais dans une gamelist, on peuple l'addgames a venir.
    /// Le clone doit donc toujours demander la mise en attente.
    /// </summary>
    [Fact]
    public void L_entree_propagee_ne_demande_jamais_d_ecriture_immediate()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan("fbneo", Path.Combine(_racine, "fbneo", "mslug.zip")),
            new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip")),
            _racine,
            Identite(),
            "genere");

        Assert.True(clone.SuppressImmediateGamelistUpdates);
    }

    [Fact]
    public void L_entree_propagee_porte_le_systeme_et_la_gamelist_de_la_cible()
    {
        var cible = new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip"));

        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan("fbneo", Path.Combine(_racine, "fbneo", "mslug.zip")),
            cible,
            _racine,
            Identite(),
            "genere");

        Assert.Equal("neogeo", clone.FrontendSystemId);
        Assert.Equal(cible.GamePath, clone.GamePath);
        Assert.Equal(Path.Combine(_racine, "neogeo", "gamelist.xml"), clone.GamelistPath);
        // Le store canonique ne change pas : c'est lui qui porte les medias partages.
        Assert.Equal("arcade", clone.SystemId);
        Assert.Equal("mslug", clone.GameSlug);
    }

    [Fact]
    public void L_identite_deja_connue_de_la_cible_est_respectee()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan("fbneo", Path.Combine(_racine, "fbneo", "mslug.zip")),
            new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip")),
            _racine,
            Identite("gameid-neogeo", "Metal Slug (NGH-201)", ["jpn"], ["ja"]),
            "gameid-neogeo");

        Assert.Equal("gameid-neogeo", clone.EsGameId);
        Assert.Equal("Metal Slug (NGH-201)", clone.DisplayName);
        Assert.Equal(["jpn"], clone.RomRegions);
        Assert.Equal(["ja"], clone.RomLanguages);
    }

    [Fact]
    public void Sans_identite_dans_la_cible_celle_de_la_source_sert_de_repli()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan("fbneo", Path.Combine(_racine, "fbneo", "mslug.zip")),
            new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip")),
            _racine,
            Identite(),
            "gameid-genere");

        Assert.Equal("gameid-genere", clone.EsGameId);
        Assert.Equal("Metal Slug - Super Vehicle-001", clone.DisplayName);
        Assert.Equal(["wor"], clone.RomRegions);
    }

    /// <summary>
    /// Le piege du portage : un chemin de media relatif est lu depuis roms/&lt;systeme&gt;.
    /// Laisse tel quel, il designerait un fichier inexistant sous le systeme cible.
    /// </summary>
    [Fact]
    public void Les_chemins_de_media_relatifs_sont_ancres_sur_le_systeme_d_origine()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan(
                "fbneo",
                Path.Combine(_racine, "fbneo", "mslug.zip"),
                new MediaNeed
                {
                    Kind = "thumbnail",
                    ExistingPath = "./../media/systems/arcade/games/mslug/artwork/screenshot.png",
                    InitialExistingPath = string.Empty,
                    ImportedPath = string.Empty
                }),
            new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip")),
            _racine,
            Identite(),
            "genere");

        var besoin = Assert.Single(clone.Needs);
        Assert.True(Path.IsPathRooted(besoin.ExistingPath));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_racine, "media", "systems", "arcade", "games", "mslug", "artwork", "screenshot.png")),
            besoin.ExistingPath);
    }

    [Fact]
    public void Un_chemin_de_media_deja_absolu_n_est_pas_touche()
    {
        var absolu = Path.Combine(_racine, "media", "systems", "arcade", "games", "mslug", "video.mp4");

        Assert.Equal(absolu, ArcadeMediaSharingService.Absolu(absolu, Path.Combine(_racine, "fbneo")));
        Assert.Equal(string.Empty, ArcadeMediaSharingService.Absolu(string.Empty, Path.Combine(_racine, "fbneo")));
        Assert.Equal(string.Empty, ArcadeMediaSharingService.Absolu(null, Path.Combine(_racine, "fbneo")));
    }

    /// <summary>La projection dans roms/ n'existe plus : seul le store canonique compte.</summary>
    [Fact]
    public void L_entree_propagee_ne_porte_aucune_projection_dans_roms()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan(
                "fbneo",
                Path.Combine(_racine, "fbneo", "mslug.zip"),
                new MediaNeed { Kind = "image", ProjectedPath = @"E:\RetroBat\roms\fbneo\images\mslug-image.png", WasProjected = true }),
            new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip")),
            _racine,
            Identite(),
            "genere");

        var besoin = Assert.Single(clone.Needs);
        Assert.Equal(string.Empty, besoin.ProjectedPath);
        Assert.False(besoin.WasProjected);
    }

    /// <summary>
    /// Le media partage reste ou il est : c'est la marque qui l'empeche d'etre recopie sous
    /// media/systems/&lt;ce systeme&gt;, donc de recreer le doublon que le partage supprime.
    /// Elle doit survivre au portage vers un autre systeme.
    /// </summary>
    [Fact]
    public void La_marque_du_media_partage_survit_au_portage()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan(
                "neogeo",
                Path.Combine(_racine, "neogeo", "mslug.zip"),
                new MediaNeed
                {
                    Kind = "thumbnail",
                    ExistingPath = Path.Combine(_racine, "media", "systems", "arcade", "games", "mslug", "artwork", "screenshot.png"),
                    IsMissing = false,
                    SharedFromSystemId = "arcade"
                }),
            new ArcadeMediaSharingService.Cible("mame", Path.Combine(_racine, "mame", "mslug.zip")),
            _racine,
            Identite(),
            "genere");

        var besoin = Assert.Single(clone.Needs);
        Assert.Equal("arcade", besoin.SharedFromSystemId);
        Assert.False(besoin.IsMissing);
    }

    [Fact]
    public void Un_media_du_store_du_systeme_n_est_pas_marque_comme_partage()
    {
        var clone = ArcadeMediaSharingService.ClonerVers(
            Plan(
                "fbneo",
                Path.Combine(_racine, "fbneo", "mslug.zip"),
                new MediaNeed { Kind = "image", ExistingPath = "/m/a.png" }),
            new ArcadeMediaSharingService.Cible("neogeo", Path.Combine(_racine, "neogeo", "mslug.zip")),
            _racine,
            Identite(),
            "genere");

        Assert.Equal(string.Empty, Assert.Single(clone.Needs).SharedFromSystemId);
    }

    // ── ne pas repeupler a l'identique ──────────────────────────────────────────────────

    [Fact]
    public void L_empreinte_des_medias_ne_depend_pas_de_l_ordre_des_besoins()
    {
        var a = Plan("fbneo", "x",
            new MediaNeed { Kind = "image", ExistingPath = "/m/a.png" },
            new MediaNeed { Kind = "thumbnail", ExistingPath = "/m/b.png" });
        var b = Plan("fbneo", "x",
            new MediaNeed { Kind = "thumbnail", ExistingPath = "/m/b.png" },
            new MediaNeed { Kind = "image", ExistingPath = "/m/a.png" });

        Assert.Equal(
            ArcadeMediaSharingService.EmpreinteDesMedias(a),
            ArcadeMediaSharingService.EmpreinteDesMedias(b));
    }

    [Fact]
    public void Un_media_qui_change_change_l_empreinte()
    {
        var avant = Plan("fbneo", "x", new MediaNeed { Kind = "image", ExistingPath = "/m/a.png" });
        var apres = Plan("fbneo", "x", new MediaNeed { Kind = "image", ExistingPath = "/m/a-v2.png" });

        Assert.NotEqual(
            ArcadeMediaSharingService.EmpreinteDesMedias(avant),
            ArcadeMediaSharingService.EmpreinteDesMedias(apres));
    }

    [Fact]
    public void Le_media_importe_prime_sur_l_existant_dans_l_empreinte()
    {
        var plan = Plan("fbneo", "x", new MediaNeed
        {
            Kind = "image",
            ExistingPath = "/m/ancien.png",
            ImportedPath = "/m/neuf.png"
        });

        Assert.Contains("neuf.png", ArcadeMediaSharingService.EmpreinteDesMedias(plan));
        Assert.DoesNotContain("ancien.png", ArcadeMediaSharingService.EmpreinteDesMedias(plan));
    }

    [Fact]
    public void Un_besoin_sans_fichier_ne_pese_pas_dans_l_empreinte()
    {
        var avec = Plan("fbneo", "x",
            new MediaNeed { Kind = "image", ExistingPath = "/m/a.png" },
            new MediaNeed { Kind = "logo", ExistingPath = string.Empty });
        var sans = Plan("fbneo", "x", new MediaNeed { Kind = "image", ExistingPath = "/m/a.png" });

        Assert.Equal(
            ArcadeMediaSharingService.EmpreinteDesMedias(sans),
            ArcadeMediaSharingService.EmpreinteDesMedias(avec));
    }

    // ── l'ordre de consultation des stores ──────────────────────────────────────────────

    [Fact]
    public void Le_store_arcade_est_consulte_en_premier()
    {
        var stores = ArcadeMediaSharingService.StoresPartages(["neogeo", "cps2", "arcade"]);

        Assert.Equal("arcade", stores[0]);
        Assert.Equal(["arcade", "neogeo", "cps2"], stores);
    }

    [Fact]
    public void Un_store_annonce_deux_fois_n_apparait_qu_une_fois()
    {
        var stores = ArcadeMediaSharingService.StoresPartages(["neogeo", "NEOGEO", "neogeo"]);

        Assert.Equal(["arcade", "neogeo"], stores);
    }

    [Fact]
    public void Sans_aucun_store_decouvert_il_reste_arcade()
    {
        Assert.Equal(["arcade"], ArcadeMediaSharingService.StoresPartages([]));
    }
}
