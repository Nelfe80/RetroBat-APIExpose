using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Les essais qui posent l'etat « RetroArch muet » tournent seuls : d'autres essais attendent un SHOW_MSG.</summary>
[CollectionDefinition(Nom, DisableParallelization = true)]
public sealed class NotificationsRetroArchCollection
{
    public const string Nom = "NotificationsRetroArch";
}

/// <summary>
/// LES NOTIFICATIONS DE RETROARCH COUPEES POUR TOUTE PARTIE LANCEE PAR NELFEPLAY (demande du user du 11/11) : le mecanisme du
/// PlayerDeck (-OnScreenMsg 0) porte par chaque lancement direct ; les messages de NelfePlay passent au Deck, sinon au bandeau
/// de l'API ; la valeur d'ES revient dans retroarch.cfg apres la partie ; rien n'entre dans l'empreinte des reglages.
/// </summary>
[Collection(NotificationsRetroArchCollection.Nom)]
public sealed class NotificationsRetroArchTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "sans-notif-" + Guid.NewGuid().ToString("N"));

    public NotificationsRetroArchTests()
    {
        Directory.CreateDirectory(_dossier);
        // Jamais le journal ni le retroarch.cfg de la borne
        NotificationsRetroArch.JournalDesSuccesDEssai = Path.Combine(_dossier, "notifications-retroarch.json");
        NotificationsRetroArch.RetroArchCfgDEssai = Path.Combine(_dossier, "retroarch.cfg");
        NotificationsRetroArch.RetroArchTourneDEssai = () => false;
    }

    public void Dispose()
    {
        NotificationsRetroArch.PoserPourEssai(false);
        NotificationsRetroArch.BandeauDEssai = null;
        NotificationsRetroArch.JournalDesSuccesDEssai = null;
        NotificationsRetroArch.RetroArchCfgDEssai = null;
        NotificationsRetroArch.RetroArchTourneDEssai = null;
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void L_argument_est_celui_du_PlayerDeck_et_ne_s_ajoute_qu_une_fois()
    {
        Assert.Equal(DeckDecor.ArgumentsSansNotifications, NotificationsRetroArch.Arguments);
        Assert.Equal(["-OnScreenMsg", "0"], NotificationsRetroArch.Ajouter(null));
        Assert.Equal(["-OnScreenMsg", "0"], NotificationsRetroArch.Ajouter([]));
        // Un Deck qui montre les messages le porte deja : pas de doublon
        var avecDeck = DeckDecor.ArgumentsDuLanceur.Concat(DeckDecor.ArgumentsSansNotifications).ToList();
        Assert.Equal(avecDeck, NotificationsRetroArch.Ajouter(avecDeck));
        // Un Deck plus ancien (sans plaque) : le decor, puis l'argument
        Assert.Equal([.. DeckDecor.ArgumentsDuLanceur, "-OnScreenMsg", "0"], NotificationsRetroArch.Ajouter(DeckDecor.ArgumentsDuLanceur));
    }

    [Theory]
    [InlineData("-p1index 0 -system nes -emulator libretro -core fceumm -OnScreenMsg 0 -rom \"E:\\r\\smb.zip\"", true)]
    [InlineData("-system nes -OnScreenMsg   false -rom x", true)]
    [InlineData("-OnScreenMsg 0", true)]
    [InlineData("-system nes -OnScreenMsg 1 -rom x", false)]
    [InlineData("-system nes -rom x", false)]
    [InlineData("-system nes -OnScreenMsgs 0 -rom x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void La_ligne_du_lanceur_dit_si_RetroArch_se_tait(string? ligne, bool attendu)
        => Assert.Equal(attendu, NotificationsRetroArch.LigneCoupe(ligne));

    [Fact]
    public void L_epreuve_de_speedrun_part_muette_avec_ou_sans_Deck_et_la_ligne_reste_valide()
    {
        var sansDeck = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur("-p1index 0", "snes", "libretro", "snes9x",
            @"E:\RetroBat\roms\snes\Super Metroid.sfc", retroAchievements: false, NotificationsRetroArch.Ajouter(null));
        Assert.NotNull(sansDeck);
        Assert.Contains("-retroachievements 0 -OnScreenMsg 0 -rom", sansDeck);
        Assert.True(NotificationsRetroArch.LigneCoupe(sansDeck));

        var avecDeck = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur("", "megadrive", "libretro", "genesis_plus_gx",
            @"E:\RetroBat\roms\megadrive\Sonic.zip", retroAchievements: false,
            NotificationsRetroArch.Ajouter(DeckDecor.ArgumentsDuLanceur.Concat(DeckDecor.ArgumentsSansNotifications)))!;
        Assert.Equal(1, Occurrences(avecDeck, "-OnScreenMsg"));
        Assert.Contains("-bezel nelfeplay-deck -ratio custom -integerscale 0 -OnScreenMsg 0", avecDeck);
    }

    [Fact]
    public void Le_MASS_RUN_part_muet()
    {
        var ligne = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur("", "nes", "libretro", "fceumm_ghosts",
            @"E:\RetroBat\roms\nes\smb.nes", null, NotificationsRetroArch.Arguments)!;
        Assert.EndsWith("-OnScreenMsg 0 -rom \"E:\\RetroBat\\roms\\nes\\smb.nes\"", ligne);
    }

    [Fact]
    public void Le_defi_du_World_Scoring_parti_par_l_API_est_muet_meme_avec_un_Deck_ancien()
    {
        var resolution = new RetroBat.Api.Netplay.EsLaunchArguments.Resolution("fbneo", "libretro", "fbneo");
        var ancien = LeaderboardInputService.ArgumentsDuDefiAvecLeDeck("", resolution, @"E:\RetroBat\roms\fbneo\mslug3.zip", DeckDecor.ArgumentsDuLanceur, [])!;
        Assert.Contains("-bezel nelfeplay-deck -ratio custom -integerscale 0 -OnScreenMsg 0 -rewind 0 -runahead 0 -autosave 0", ancien);

        var recent = LeaderboardInputService.ArgumentsDuDefiAvecLeDeck("", resolution, @"E:\RetroBat\roms\fbneo\mslug3.zip",
            [.. DeckDecor.ArgumentsDuLanceur, .. DeckDecor.ArgumentsSansNotifications], RetroBat.Api.Netplay.NetplayHostService.ArgumentsDeLHote)!;
        Assert.Equal(1, Occurrences(recent, "-OnScreenMsg"));
        Assert.Contains("-netplaymode host", recent);
    }

    [Fact]
    public void Le_MULTIPLAYER_part_muet_chez_l_hote_comme_chez_l_invite()
    {
        var jeu = new MultiplayerGame { Id = "arcade/mslug3", System = "arcade", Set = "mslug3", Players = 2 };
        var hote = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\r\mslug3.zip", jeu, 1,
            MultiplayerLaunchPlan.ArgumentsHote("NELFEPLAY_player_ab12cd34", "aaaa1111", "bbbb2222", "madrid"))!;
        var invite = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\r\mslug3.zip", jeu, 2, ["-netplaymode", "client"])!;
        foreach (var ligne in new[] { hote, invite })
        {
            Assert.True(NotificationsRetroArch.LigneCoupe(ligne));
            Assert.Contains("-retroachievements 0 -OnScreenMsg 0", ligne);
            // Jamais d'argument en « -p » (le lanceur le prendrait pour une manette)
            Assert.DoesNotContain(" -p", ligne.Replace(" -p1", "", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Rien_n_entre_dans_l_empreinte_des_options_du_coeur()
    {
        // video_font_enable est un reglage de RetroArch, jamais une option du coeur : le filtre de l'empreinte l'ecarte de toute facon
        var filtre = NelfePlayScoringReporter.FilterGameplayCoreOptions("fbneo-cpu-speed-adjust=100%;video_font_enable=false;OnScreenMsg=0");
        Assert.DoesNotContain("video_font_enable", filtre);
        Assert.DoesNotContain("OnScreenMsg", filtre);
        // et les fonctions qui font refuser un score restent celles des reglages certifies
        Assert.Equal(["-rewind", "0", "-runahead", "0", "-autosave", "0"], LeaderboardInputService.FonctionsNeutraliseesDuDefi);
    }

    [Theory]
    [InlineData("video_font_enable = \"false\"", "true", true, "true")]
    [InlineData("video_font_enable = \"false\"", "false", false, "false")]
    [InlineData("video_font_enable = \"true\"", "true", false, "true")]
    public void La_valeur_d_ES_revient_dans_retroarch_cfg_apres_la_partie(string ligne, string valeurDEs, bool change, string attendu)
    {
        var cfg = Path.Combine(_dossier, "retroarch.cfg");
        File.WriteAllText(cfg, "video_fullscreen = \"true\"\n" + ligne + "\nrewind_enable = \"false\"\n");
        Assert.Equal(change, NotificationsRetroArch.Rendre(cfg, valeurDEs));
        Assert.Equal(attendu, RetroArchConfigEdit.Lire(cfg, NotificationsRetroArch.CleRetroArch));
        // le reste du fichier ne bouge pas
        Assert.Equal("true", RetroArchConfigEdit.Lire(cfg, "video_fullscreen"));
        Assert.Equal("false", RetroArchConfigEdit.Lire(cfg, "rewind_enable"));
    }

    [Fact]
    public void Sans_fichier_ni_cle_rien_ne_s_ecrit()
    {
        var cfg = Path.Combine(_dossier, "absent.cfg");
        Assert.False(NotificationsRetroArch.Rendre(cfg, "true"));
        Assert.False(File.Exists(cfg));
        File.WriteAllText(cfg, "video_fullscreen = \"true\"\n");
        Assert.False(NotificationsRetroArch.Rendre(cfg, "true"));
        Assert.Null(RetroArchConfigEdit.Lire(cfg, NotificationsRetroArch.CleRetroArch));
    }

    [Fact]
    public void RetroArch_muet_les_messages_de_NelfePlay_passent_au_bandeau_sinon_a_RetroArch()
    {
        var vus = new List<(string Marque, string Texte, int Ms, bool Alerte)>();
        NotificationsRetroArch.BandeauDEssai = (m, t, ms, a) => vus.Add((m, t, ms, a));

        // RetroArch parle (partie lancee par ES, ou hors NelfePlay) : l'appelant envoie SHOW_MSG comme avant
        NotificationsRetroArch.PoserPourEssai(false);
        Assert.False(NotificationsRetroArch.AuBandeau("SPEEDRUN", "Attempt failed."));
        Assert.Empty(vus);

        // RetroArch muet : le bandeau, sur une ligne
        NotificationsRetroArch.PoserPourEssai(true);
        Assert.True(NotificationsRetroArch.AuBandeau("MULTIPLAYER", "No controller\r\nfound", 10000, alerte: true));
        Assert.Equal([("MULTIPLAYER", "No controller found", 10000, true)], vus);
        // un message vide ne s'affiche pas
        Assert.False(NotificationsRetroArch.AuBandeau("SPEEDRUN", "  "));
    }

    [Fact]
    public void Un_lancement_sans_l_argument_ou_qui_ne_part_pas_laisse_RetroArch_parler()
    {
        NotificationsRetroArch.PoserPourEssai(true);
        // le labo (commands/launch) ou un lancement sans l'argument : RetroArch reparle
        NotificationsRetroArch.Suivre("-system mame -emulator libretro -core mame -rom x", null, null);
        Assert.False(NotificationsRetroArch.Coupees);
        // le lanceur n'a pas demarre : rien de coupe
        NotificationsRetroArch.Suivre("-system nes -OnScreenMsg 0 -rom x", null, null);
        Assert.False(NotificationsRetroArch.Coupees);
    }

    // ─────────────── RetroAchievements visibles en World Scoring (14/11) ───────────────

    [Fact]
    public void Sauf_succes_les_widgets_restent_et_seuls_les_autres_messages_sont_coupes()
    {
        var cles = NotificationsRetroArch.AutresNotifications.Select(r => r.Key).ToList();
        // le commutateur general et les widgets ne sont jamais touches : ce sont eux qui portent les succes
        Assert.DoesNotContain(NotificationsRetroArch.CleRetroArch, cles);
        Assert.DoesNotContain("menu_enable_widgets", cles);
        // vrai, il cacherait TOUT en jeu, succes compris
        Assert.DoesNotContain("notification_show_when_menu_is_alive", cles);
        Assert.DoesNotContain(cles, c => c.StartsWith("cheevos_", StringComparison.Ordinal));
        Assert.Contains("notification_show_save_state", cles);
        Assert.Contains("notification_show_screenshot", cles);
        Assert.Contains("notification_show_fast_forward", cles);
        Assert.Equal(cles.Count, cles.Distinct().Count());
        // le flash de la capture : 2 = OFF ; les autres a faux
        Assert.All(NotificationsRetroArch.AutresNotifications,
            r => Assert.Equal(r.Key == "notification_show_screenshot_flash" ? "2" : "false", r.Value));
    }

    [Fact]
    public void Sauf_succes_l_argument_du_Deck_qui_coupe_tout_part_et_la_ligne_reste_valide()
    {
        var avecDeck = NotificationsRetroArch.AjouterSaufSucces([.. DeckDecor.ArgumentsDuLanceur, .. DeckDecor.ArgumentsSansNotifications]);
        Assert.DoesNotContain("-OnScreenMsg", avecDeck);
        Assert.Equal([.. DeckDecor.ArgumentsDuLanceur, .. NotificationsRetroArch.ArgumentsSaufSucces], avecDeck);
        Assert.Equal(NotificationsRetroArch.ArgumentsSaufSucces, NotificationsRetroArch.AjouterSaufSucces(null));

        var ligne = RetroBat.Api.Controllers.CommandsController.ArgumentsDuLanceur("-p1index 0", "fbneo", "libretro", "fbneo",
            @"E:\RetroBat\roms\fbneo\shinobi.zip", null, avecDeck);
        Assert.NotNull(ligne);
        Assert.Contains("-retroarch.notification_show_save_state false", ligne);
        Assert.Contains("-retroarch.notification_show_screenshot_flash 2", ligne);
        Assert.False(NotificationsRetroArch.LigneCoupe(ligne));
        Assert.True(NotificationsRetroArch.LigneGardeLesSucces(ligne));
    }

    [Fact]
    public void Le_defi_du_World_Scoring_d_un_joueur_RetroAchievements_garde_les_succes()
    {
        var resolution = new RetroBat.Api.Netplay.EsLaunchArguments.Resolution("fbneo", "libretro", "fbneo");
        var ligne = LeaderboardInputService.ArgumentsDuDefiAvecLeDeck("", resolution, @"E:\RetroBat\roms\fbneo\shinobi.zip",
            [.. DeckDecor.ArgumentsDuLanceur, .. DeckDecor.ArgumentsSansNotifications], [], succes: true)!;
        Assert.DoesNotContain("-OnScreenMsg", ligne);
        Assert.Contains("-bezel nelfeplay-deck -ratio custom -integerscale 0 -retroarch.notification_show_autoconfig false", ligne);
        // les fonctions qui font refuser un score restent coupees
        Assert.Contains("-retroarch.notification_show_netplay_extra false -rewind 0 -runahead 0 -autosave 0", ligne);
        Assert.True(NotificationsRetroArch.LigneGardeLesSucces(ligne));

        // sans RetroAchievements : comme avant, tout coupe
        var sans = LeaderboardInputService.ArgumentsDuDefiAvecLeDeck("", resolution, @"E:\RetroBat\roms\fbneo\shinobi.zip",
            [.. DeckDecor.ArgumentsDuLanceur, .. DeckDecor.ArgumentsSansNotifications], [])!;
        Assert.True(NotificationsRetroArch.LigneCoupe(sans));
        Assert.DoesNotContain("-retroarch.", sans);
    }

    [Theory]
    [InlineData("-system fbneo -retroarch.notification_show_save_state false -rom x", true)]
    [InlineData("-retroarch.notification_show_save_state 0", true)]
    [InlineData("-system fbneo -retroarch.notification_show_save_state true -rom x", false)]
    [InlineData("-system fbneo -retroarch.notification_show_save_states false -rom x", false)]
    [InlineData("-system fbneo -OnScreenMsg 0 -rom x", false)]
    [InlineData(null, false)]
    public void La_ligne_du_lanceur_dit_si_les_succes_restent(string? ligne, bool attendu)
        => Assert.Equal(attendu, NotificationsRetroArch.LigneGardeLesSucces(ligne));

    [Theory]
    [InlineData("collection", true)]
    [InlineData("collection (carrousel inconnu)", true)]
    [InlineData("defi", true)]
    [InlineData("site", true)]
    [InlineData("api", true)]
    [InlineData("speedrun", false)]
    [InlineData("concours", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Seules_les_parties_du_World_Scoring_gardent_les_succes(string? origine, bool attendu)
        => Assert.Equal(attendu, NotificationsRetroArch.EstDuWorldScoring(origine));

    [Theory]
    [InlineData("global.retroachievements=true;global.retroachievements.username=Snap", true)]
    [InlineData("global.retroachievements=1;global.retroachievements.username=Snap", true)]
    [InlineData("global.retroachievements=true", false)]
    [InlineData("global.retroachievements=true;global.retroachievements.username= ", false)]
    [InlineData("global.retroachievements=false;global.retroachievements.username=Snap", false)]
    [InlineData("global.retroachievements.username=Snap", false)]
    [InlineData("global.retroachievements=true;global.retroachievements.username=Snap;fbneo.retroachievements=false", false)]
    [InlineData("global.retroachievements=false;global.retroachievements.username=Snap;fbneo[\"shinobi.zip\"].retroachievements=true", true)]
    [InlineData("global.retroachievements=true;global.retroachievements.username=Snap;fbneo[\"shinobi.zip\"].retroachievements=0", false)]
    [InlineData("global.retroachievements=true;global.retroachievements.username=Snap;mame.retroachievements=false", true)]
    [InlineData("", false)]
    public void Le_joueur_a_RetroAchievements_comme_le_lanceur_le_lit(string reglages, bool attendu)
    {
        var doc = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("config",
            reglages.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('=', 2)).Select(kv =>
                new System.Xml.Linq.XElement("string", new System.Xml.Linq.XAttribute("name", kv[0]), new System.Xml.Linq.XAttribute("value", kv[1])))));
        Assert.Equal(attendu, NotificationsRetroArch.SuccesActifs(doc, "fbneo", @"E:\RetroBat\roms\fbneo\shinobi.zip"));

        // lu sur le disque, en lecture seule ; un fichier absent ne vaut pas RetroAchievements
        var es = Path.Combine(_dossier, "es_settings.cfg");
        doc.Save(es);
        var octets = File.ReadAllBytes(es);
        Assert.Equal(attendu, NotificationsRetroArch.SuccesActifs("fbneo", @"E:\RetroBat\roms\fbneo\shinobi.zip", es));
        Assert.Equal(octets, File.ReadAllBytes(es));
        Assert.False(NotificationsRetroArch.SuccesActifs("fbneo", "shinobi.zip", Path.Combine(_dossier, "absent.cfg")));
    }

    [Fact]
    public void Les_reglages_du_joueur_reviennent_dans_retroarch_cfg_RetroArch_ferme()
    {
        var cfg = NotificationsRetroArch.RetroArchCfgDEssai!;
        var journal = NotificationsRetroArch.JournalDesSuccesDEssai!;
        File.WriteAllText(cfg, "video_font_enable = \"true\"\nnotification_show_save_state = \"true\"\n"
            + "notification_show_refresh_rate = \"true\"\nnotification_show_screenshot_flash = \"0\"\n");
        NotificationsRetroArch.NoterAvantLesSucces();
        Assert.True(File.Exists(journal));

        // le lanceur ecrit les valeurs de la partie (et une cle que le joueur n'avait pas)
        RetroArchConfigEdit.Poser(cfg, NotificationsRetroArch.AutresNotifications.ToDictionary(r => r.Key, r => r.Value));
        // une seconde note (partie suivante avant le rendu) ne remplace pas la valeur du joueur
        NotificationsRetroArch.NoterAvantLesSucces();

        // RetroArch tourne encore : rien
        NotificationsRetroArch.RetroArchTourneDEssai = () => true;
        Assert.False(NotificationsRetroArch.RendreLesAutresNotifications());
        Assert.Equal("false", RetroArchConfigEdit.Lire(cfg, "notification_show_save_state"));

        NotificationsRetroArch.RetroArchTourneDEssai = () => false;
        Assert.True(NotificationsRetroArch.RendreLesAutresNotifications());
        Assert.Equal("true", RetroArchConfigEdit.Lire(cfg, "notification_show_save_state"));
        Assert.Equal("true", RetroArchConfigEdit.Lire(cfg, "notification_show_refresh_rate"));
        Assert.Equal("0", RetroArchConfigEdit.Lire(cfg, "notification_show_screenshot_flash"));
        Assert.Null(RetroArchConfigEdit.Lire(cfg, "notification_show_netplay_extra"));
        Assert.Equal("true", RetroArchConfigEdit.Lire(cfg, "video_font_enable"));
        Assert.False(File.Exists(journal));
        Assert.False(NotificationsRetroArch.RendreLesAutresNotifications());
    }

    private static int Occurrences(string texte, string motif)
    {
        var n = 0;
        for (var i = texte.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = texte.IndexOf(motif, i + motif.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
