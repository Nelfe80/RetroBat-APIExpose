using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Ce qu'une borne prepare pour une partie MULTIPLAYER (lot M4) : la ligne du lanceur, les options du
/// coeur, la place netplay, le recadrage d'un jeu a deux moniteurs, et le retour des reglages apres.
/// </summary>
public class MultiplayerLaunchPlanTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "mp-lancement-" + Guid.NewGuid().ToString("N")[..8]);

    public MultiplayerLaunchPlanTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly MultiplayerGame Mslug3 = new() { Id = "arcade/mslug3", System = "arcade", Set = "mslug3", Players = 2 };

    private static readonly MultiplayerGame Wrally2 = new()
    {
        Id = "arcade/wrally2", System = "arcade", Set = "wrally2", Players = 2, Screens = 2, SeatScreens = [1, 2],
    };

    [Fact]
    public void L_hote_impose_son_coeur_coupe_les_succes_et_passe_tout_le_netplay_en_arguments()
    {
        var netplay = MultiplayerLaunchPlan.ArgumentsHote("NELFEPLAY_player_ab12cd34", "aaaa1111", "bbbb2222", "madrid");
        var ligne = MultiplayerLaunchPlan.Arguments("-p1index 0", "fbneo", "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip", Mslug3, 2, netplay);

        Assert.Equal(
            "-p1index 0 -system fbneo -emulator libretro -core fbneo -retroachievements 0"
            + " -rewind 0 -autosave 0 -runahead 0 -fastforward_ratio 1.000000 -netplay true -netplaymode host"
            + " -netplay.nickname NELFEPLAY_player_ab12cd34 -netplay.password aaaa1111 -netplay.spectatepassword bbbb2222"
            + " -netplay.relay madrid -netplay.port 55435 -netplay_public_announce true -rom \"E:\\RetroBat\\roms\\fbneo\\mslug3.zip\"",
            ligne);
    }

    [Fact]
    public void Les_reglages_que_le_lanceur_reecrirait_passent_sur_sa_ligne_sans_cle_en_p()
    {
        // Micro Mages : sans l'adaptateur 4 joueurs sur la ligne, le lanceur remettait « auto » (plus de joueurs 3 et 4).
        var jeu = new MultiplayerGame
        {
            Id = "nes/micro-mages", System = "nes", Players = 4,
            LauncherOptions = new Dictionary<string, string>
            {
                ["nestopia_select_adapter"] = "ntsc",
                ["pce_multitap"] = "enabled",
                ["douteux"] = "a b",
                ["autre_reglage"] = "1",
            },
        };

        var ligne = MultiplayerLaunchPlan.Arguments("", "nes", "nestopia", @"E:\RetroBat\roms\nes\Micro Mages.zip", jeu, 1, [])!;

        Assert.Contains(" -fastforward_ratio 1.000000 -autre_reglage 1 -nestopia_select_adapter ntsc -rom ", ligne);
        Assert.DoesNotContain("pce_multitap", ligne);
        Assert.DoesNotContain("douteux", ligne);
    }

    [Fact]
    public void Aucune_cle_de_la_ligne_ne_commence_par_p_hors_manettes()
    {
        // Le lanceur prend tout « -p... » pour une manette : « -preemptive_frames » cassait tout netplay (23/10).
        var session = new MultiplayerSessionNetplay { Relay = "europe-west1.relay.retroarch.com", Port = 55435, Session = "s3ss10n", PlayerPassword = "aaaa1111" };
        foreach (var netplay in new[]
                 {
                     MultiplayerLaunchPlan.ArgumentsHote("NELFEPLAY_player_ab12cd34", "aaaa1111", "bbbb2222", "madrid"),
                     MultiplayerLaunchPlan.ArgumentsInvite("NELFEPLAY_player2_01ff", session),
                 })
        {
            var ligne = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\RetroBat\roms\fbneo\wrally2.zip", Wrally2, 2, netplay)!;
            Assert.DoesNotContain(ligne.Split(' '), morceau => morceau.StartsWith("-p", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Le_journal_nomme_les_manettes_sans_chemin_ni_identifiant()
    {
        var ligne = "-p1index 0 -p1guid 03000000 -p1path \"HID#VID_1\" -p1name \"Generic USB Joystick\" -p1nbbuttons 12"
            + " -p2index 1 -p2guid 0300ab -p2path \"HID#VID_2\" -p2name \"vJoy Device\" -p2nbbuttons 8";
        Assert.Equal("P1 Generic USB Joystick, P2 vJoy Device", MultiplayerLaunchPlan.NomsDesManettes(ligne));
        Assert.StartsWith("aucune", MultiplayerLaunchPlan.NomsDesManettes(""));
    }

    [Fact]
    public void L_invite_rejoint_la_session_de_l_hote_a_sa_place()
    {
        var session = new MultiplayerSessionNetplay { Relay = "europe-west1.relay.retroarch.com", Port = 55435, Session = "s3ss10n", PlayerPassword = "aaaa1111" };
        var ligne = MultiplayerLaunchPlan.Arguments("", "gba", "mgba_splitscreen", @"E:\RetroBat\roms\gba\Doom II (USA).zip",
            new MultiplayerGame { Id = "gba/doom-ii", System = "gba", Players = 4 }, 3,
            MultiplayerLaunchPlan.ArgumentsInvite("NELFEPLAY_player2_01ff", session));

        Assert.Contains("-netplaymode client -netplayip europe-west1.relay.retroarch.com -netplayport 55435 -netplaysession s3ss10n -netplaypass aaaa1111", ligne);
        Assert.Contains("-core mgba_splitscreen", ligne);
        Assert.EndsWith("-rom \"E:\\RetroBat\\roms\\gba\\Doom II (USA).zip\"", ligne);
        Assert.DoesNotContain("-shader", ligne);
    }

    [Fact]
    public void Un_jeu_a_deux_moniteurs_montre_l_ecran_de_la_place_en_4_3_sans_habillage()
    {
        var ligne = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\r\wrally2.zip", Wrally2, 2, []);

        Assert.Contains("-shaderset none -shader nelfeplay/half-2-of-2 -ratio 4/3 -bezel none", ligne);
        Assert.Equal("nelfeplay/half-1-of-2", MultiplayerLaunchPlan.ShaderDeLaPlace(Wrally2, 1));
        var fichiers = MultiplayerLaunchPlan.FichiersDeShader(2, 2);
        Assert.Contains("TexCoord.x * 0.500000 + 0.500000", fichiers["half-2-of-2.slang"]);
        Assert.Contains("shader0 = half-2-of-2.slang", fichiers["half-2-of-2.slangp"]);
        Assert.Contains("plein.x * 0.500000", fichiers["half-2-of-2.glsl"]);
    }

    /// <summary>Super Mario Kart : la place 1 en haut, la place 2 en bas (26/10).</summary>
    private static readonly MultiplayerGame MarioKart = new()
    {
        Id = "snes/super-mario-kart", System = "snes", Players = 2,
        Views = [new() { Seat = 1, X = 0, Y = 0, W = 1, H = 0.5 }, new() { Seat = 2, X = 0, Y = 0.5, W = 1, H = 0.5 }],
    };

    /// <summary>GoldenEye 007 : deux bandes a deux, quatre quarts a trois et quatre.</summary>
    private static readonly MultiplayerGame GoldenEye = new()
    {
        Id = "n64/goldeneye-007", System = "n64", Players = 4,
        Views =
        [
            new() { Seat = 1, Players = 2, X = 0, Y = 0, W = 1, H = 0.5 }, new() { Seat = 2, Players = 2, X = 0, Y = 0.5, W = 1, H = 0.5 },
            new() { Seat = 1, X = 0, Y = 0, W = 0.5, H = 0.5 }, new() { Seat = 2, X = 0.5, Y = 0, W = 0.5, H = 0.5 },
            new() { Seat = 3, X = 0, Y = 0.5, W = 0.5, H = 0.5 }, new() { Seat = 4, X = 0.5, Y = 0.5, W = 0.5, H = 0.5 },
        ],
    };

    [Fact]
    public void Un_ecran_partage_ne_montre_que_la_vue_de_la_place_a_son_format()
    {
        var ligne = MultiplayerLaunchPlan.Arguments("", "snes", "snes9x", @"E:\r\smk.zip", MarioKart, 2, [])!;

        Assert.Contains("-shaderset none -shader nelfeplay/view-0000-0500-1000-0500 -ratio config -bezel none", ligne);
        // La moitie basse d'une image 4/3 : 8/3.
        Assert.Equal("2.666667", MultiplayerLaunchPlan.FormatPourRetroArch(MarioKart, MarioKart.VueDeLaPlace(2, 2)!));
        var fichiers = MultiplayerLaunchPlan.FichiersDeLaVue(MarioKart.VueDeLaPlace(2, 2)!);
        Assert.Contains("TexCoord.x * 1.000000 + 0.000000, TexCoord.y * 0.500000 + 0.500000", fichiers["view-0000-0500-1000-0500.slang"]);
        Assert.Contains("plein.y * 0.500000", fichiers["view-0000-0500-1000-0500.glsl"]);
    }

    [Fact]
    public void La_vue_depend_du_nombre_de_joueurs()
    {
        Assert.Equal("nelfeplay/view-0000-0000-1000-0500", MultiplayerLaunchPlan.ShaderDeLaPlace(GoldenEye, 1, 2));
        Assert.Equal("nelfeplay/view-0000-0000-0500-0500", MultiplayerLaunchPlan.ShaderDeLaPlace(GoldenEye, 1, 4));
        Assert.Equal("nelfeplay/view-0500-0500-0500-0500", MultiplayerLaunchPlan.ShaderDeLaPlace(GoldenEye, 4, 4));
        // Un quart d'image 4/3 garde le 4/3.
        Assert.Equal("1.333333", MultiplayerLaunchPlan.FormatPourRetroArch(GoldenEye, GoldenEye.VueDeLaPlace(3, 3)!));
    }

    [Fact]
    public void Le_shader_des_moniteurs_cote_a_cote_ne_change_pas_de_calcul()
        => Assert.Contains("TexCoord.x * 0.500000 + 0.500000, TexCoord.y * 1.000000 + 0.000000",
            MultiplayerLaunchPlan.FichiersDeShader(2, 2)["half-2-of-2.slang"]);

    [Fact]
    public void Des_vues_coherentes_passent()
    {
        Assert.Null(MultiplayerCatalogueStore.ValiderLesVues(MarioKart));
        Assert.Null(MultiplayerCatalogueStore.ValiderLesVues(GoldenEye));
    }

    [Fact]
    public void Une_place_sans_vue_ferait_voir_l_ecran_des_autres()
    {
        var sansQuatre = GoldenEye with { Views = GoldenEye.Views!.Where(vue => vue.Seat != 4).ToList() };
        Assert.Contains("la place 4 n'a pas de vue a 4 joueurs", MultiplayerCatalogueStore.ValiderLesVues(sansQuatre));
    }

    [Fact]
    public void Une_vue_hors_de_l_image_est_refusee()
    {
        var deborde = MarioKart with { Views = [new() { Seat = 1, Y = 0, H = 0.6 }, new() { Seat = 2, Y = 0.5, H = 0.6 }] };
        Assert.Contains("hors de l'image", MultiplayerCatalogueStore.ValiderLesVues(deborde));
    }

    [Theory]
    [InlineData(null, "1.9.96", true)]
    [InlineData("1.9.97", "1.9.96", false)]
    [InlineData("1.9.97", "1.9.97", true)]
    [InlineData("1.9.97+20261026", "1.9.98", true)]
    [InlineData("pas une version", "1.9.98", false)]
    public void Un_jeu_n_entre_que_chez_une_API_assez_recente(string? minimum, string api, bool admis)
        => Assert.Equal(admis, (MarioKart with { MinApi = minimum }).AdmisPar(Version.Parse(api)));

    [Theory]
    [InlineData("fbneo core")]
    [InlineData("-netplay")]
    [InlineData("a\"b")]
    public void Un_nom_qui_n_en_est_pas_un_ne_passe_pas(string coeur)
        => Assert.Null(MultiplayerLaunchPlan.Arguments("", "fbneo", coeur, @"E:\r\x.zip", Mslug3, 1, []));

    [Theory]
    [InlineData("-x")]
    [InlineData("deux mots")]
    [InlineData("")]
    public void Une_valeur_netplay_douteuse_est_refusee_plutot_que_glissee(string motDePasse)
        => Assert.Throws<ArgumentException>(() => MultiplayerLaunchPlan.ArgumentsHote("NELFEPLAY_x", motDePasse, "b", "madrid"));

    [Fact]
    public void Les_options_du_coeur_recoivent_le_nombre_de_joueurs_et_la_place()
    {
        var coeur = new MultiplayerCore
        {
            Core = "mgba_splitscreen",
            Options = new Dictionary<string, string> { ["splitscreen_players"] = "{players}", ["splitscreen_boot_menu"] = "off" },
            SeatOptions = new Dictionary<string, string> { ["splitscreen_focus_player"] = "{seat}", ["splitscreen_audio"] = "player {seat}" },
        };

        var options = MultiplayerLaunchPlan.OptionsDuCoeur(coeur, 4, 3);

        Assert.Equal("4", options["splitscreen_players"]);
        Assert.Equal("off", options["splitscreen_boot_menu"]);
        Assert.Equal("3", options["splitscreen_focus_player"]);
        Assert.Equal("player 3", options["splitscreen_audio"]);
    }

    [Fact]
    public void Chaque_borne_demande_sa_manette_et_seulement_elle()
    {
        var place = MultiplayerLaunchPlan.Place(2);
        Assert.Equal("true", place["netplay_request_device_p2"]);
        Assert.Equal(7, place.Values.Count(v => v == "false"));
    }

    [Fact]
    public void Un_boxeur_de_Title_Fight_tient_deux_ports_et_les_autres_jeux_un_seul()
    {
        var titlef = new MultiplayerGame { Id = "arcade/titlef", System = "arcade", Set = "titlef", Players = 2, SeatPorts = [[1, 3], [2, 4]] };
        Assert.Equal([2, 4], titlef.PortsDeLaPlace(2));
        var place = MultiplayerLaunchPlan.Place(titlef.PortsDeLaPlace(2));
        Assert.Equal(["netplay_request_device_p2", "netplay_request_device_p4"], place.Where(p => p.Value == "true").Select(p => p.Key).OrderBy(k => k));
        Assert.Equal([3], new MultiplayerGame { Players = 4 }.PortsDeLaPlace(3));

        // La validation du catalogue : voir MultiplayerCollectionSyncTests.Un_catalogue_douteux_est_refuse.
    }

    [Fact]
    public void Les_reglages_poses_sont_rendus_et_une_cle_ajoutee_repart()
    {
        var fichier = Path.Combine(_racine, "retroarch.cfg");
        File.WriteAllText(fichier, "video_driver = \"d3d11\"\nnetplay_request_device_p1 = \"false\"\n");

        var avant = RetroArchConfigEdit.Poser(fichier, new Dictionary<string, string>
        {
            ["netplay_request_device_p1"] = "true",
            ["splitscreen_layout"] = "focus",
        });
        Assert.Equal("true", RetroArchConfigEdit.Lire(fichier, "netplay_request_device_p1"));
        Assert.Equal("focus", RetroArchConfigEdit.Lire(fichier, "splitscreen_layout"));
        Assert.Equal("d3d11", RetroArchConfigEdit.Lire(fichier, "video_driver"));

        RetroArchConfigEdit.Rendre(fichier, avant);
        Assert.Equal("video_driver = \"d3d11\"\nnetplay_request_device_p1 = \"false\"\n", File.ReadAllText(fichier));
    }

    [Fact]
    public void Une_partie_interrompue_rend_ses_reglages_au_demarrage_suivant()
    {
        var fichier = Path.Combine(_racine, "retroarch-core-options.cfg");
        var journal = Path.Combine(_racine, "state", "multiplayer-lancement.json");
        File.WriteAllText(fichier, "splitscreen_boot_menu = \"on\"\n");
        var avant = RetroArchConfigEdit.Poser(fichier, new Dictionary<string, string> { ["splitscreen_boot_menu"] = "off" });
        RetroArchConfigEdit.Noter(journal, new Dictionary<string, Dictionary<string, string?>> { [fichier] = avant });

        // L'API s'arrete pendant la partie ; au demarrage suivant :
        Assert.Equal(1, RetroArchConfigEdit.RendreLeJournal(journal));
        Assert.Equal("on", RetroArchConfigEdit.Lire(fichier, "splitscreen_boot_menu"));
        Assert.False(File.Exists(journal));
        Assert.Equal(0, RetroArchConfigEdit.RendreLeJournal(journal));
    }

    [Fact]
    public void Une_cle_au_nom_plus_long_n_est_pas_confondue()
    {
        var fichier = Path.Combine(_racine, "x.cfg");
        File.WriteAllText(fichier, "netplay_request_device_p10 = \"true\"\n");
        RetroArchConfigEdit.Poser(fichier, new Dictionary<string, string> { ["netplay_request_device_p1"] = "false" });
        Assert.Equal("true", RetroArchConfigEdit.Lire(fichier, "netplay_request_device_p10"));
        Assert.Equal("false", RetroArchConfigEdit.Lire(fichier, "netplay_request_device_p1"));
    }

    [Fact]
    public void Les_DIP_non_fixes_partent_le_temps_de_la_partie_puis_reviennent()
    {
        var fichier = Path.Combine(Path.GetTempPath(), "mp-dip-" + Guid.NewGuid().ToString("N") + ".cfg");
        File.WriteAllLines(fichier,
        [
            "fbneo-dipswitch-sf2ce-Difficulty = \"7 (Hardest)\"",
            "fbneo-dipswitch-sf2ce-Free_Play = \"On\"",
            "fbneo-dipswitch-mslug3-Difficulty = \"8\"",
            "fbneo-cpu-speed-adjust = \"100%\"",
        ]);
        try
        {
            var jeu = new MultiplayerGame { Id = "arcade/sf2ce", System = "arcade", Set = "sf2ce", Players = 2 };
            var prefixe = MultiplayerLaunchPlan.PrefixeDesDip(jeu, "fbneo");
            Assert.Equal("fbneo-dipswitch-sf2ce-", prefixe);
            Assert.Null(MultiplayerLaunchPlan.PrefixeDesDip(jeu with { System = "gba", Set = null }, "mgba_splitscreen"));

            var imposees = new Dictionary<string, string> { ["fbneo-dipswitch-sf2ce-Free_Play"] = "Off" };
            var avant = RetroArchConfigEdit.Poser(fichier, imposees);
            foreach (var (cle, valeur) in RetroArchConfigEdit.Retirer(fichier, cle => cle.StartsWith(prefixe!, StringComparison.Ordinal) && !imposees.ContainsKey(cle)))
            {
                avant.TryAdd(cle, valeur);
            }

            // Pendant la partie : la difficulte du joueur est partie (le coeur prend sa valeur par defaut), le DIP fixe
            // par le catalogue est pose, les DIP des autres jeux et les autres options ne bougent pas.
            Assert.Null(RetroArchConfigEdit.Lire(fichier, "fbneo-dipswitch-sf2ce-Difficulty"));
            Assert.Equal("Off", RetroArchConfigEdit.Lire(fichier, "fbneo-dipswitch-sf2ce-Free_Play"));
            Assert.Equal("8", RetroArchConfigEdit.Lire(fichier, "fbneo-dipswitch-mslug3-Difficulty"));
            Assert.Equal("100%", RetroArchConfigEdit.Lire(fichier, "fbneo-cpu-speed-adjust"));

            // Apres : tout est rendu.
            RetroArchConfigEdit.Rendre(fichier, avant);
            Assert.Equal("7 (Hardest)", RetroArchConfigEdit.Lire(fichier, "fbneo-dipswitch-sf2ce-Difficulty"));
            Assert.Equal("On", RetroArchConfigEdit.Lire(fichier, "fbneo-dipswitch-sf2ce-Free_Play"));
        }
        finally
        {
            File.Delete(fichier);
        }
    }

    [Fact]
    public void Un_emulateur_deja_ouvert_ne_passe_pas_pour_le_jeu_qui_demarre()
    {
        // 25/10 : un RetroArch ouvert avant le lancement faisait effacer « Les joueurs se synchronisent… » 48 s trop tot.
        // Aucun processus n'a pu demarrer dans une heure : avec cette borne, rien ne compte.
        Assert.False(RetroBat.Api.Infrastructure.EmulatorForeground.EmulateurDeJeuTourne(DateTime.Now.AddHours(1)));
    }
}
