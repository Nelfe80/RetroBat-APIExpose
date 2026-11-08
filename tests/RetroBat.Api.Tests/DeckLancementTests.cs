using System.Text.Json;
using RetroBat.Api.Controllers;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE LANCEMENT AVEC LE PLAYERDECK (CDC PlayerDeck H-D15 a H-D20) : le decor nelfeplay-deck et son .info, le cadre que le
/// lanceur de RetroBat en tire (son calcul reproduit) et que RetroArch applique, les Decks installes, le choix retenu par
/// joueur, jeu et mode, le geste en jeu, et les arguments ajoutes aux lancements des boutons speciaux.
/// </summary>
public sealed class DeckLancementTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "deck-lancement-" + Guid.NewGuid().ToString("N"));

    public DeckLancementTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    // ─────────────────────────── le .info et le cadre du lanceur ───────────────────────────

    /// <summary>Les trous rendus par le Deck (--render-decor, 08/11) : 19XX centre, Metal Slug 3 pousse a droite.</summary>
    public static TheoryData<string, int, int, int, int, int, int, int, int> Trous => new()
    {
        // jeu, ecran, trou, taille native du jeu (tournee)
        { "19xx", 1920, 1200, 510, 0, 900, 1200, 224, 384 },
        { "19xx", 1920, 1080, 555, 0, 810, 1080, 224, 384 },
        { "mslug3", 1920, 1200, 476, 58, 1444, 1083, 304, 224 },
        { "mslug3", 1920, 1080, 480, 0, 1440, 1080, 304, 224 },
    };

    [Theory]
    [MemberData(nameof(Trous))]
    public void Le_lanceur_tire_du_info_un_cadre_egal_au_trou_et_RetroArch_y_cale_le_jeu(string jeu, int ecranL, int ecranH, int x, int y, int w, int h, int natifL, int natifH)
    {
        var info = JsonDocument.Parse(DeckDecor.Info(ecranL, ecranH, new DeckDecor.Trou(x, y, w, h))).RootElement;
        int I(string n) => info.GetProperty(n).GetInt32();
        Assert.Equal(ecranL, I("width"));
        Assert.Equal(ecranH, I("height"));
        Assert.Equal(x, I("left"));
        Assert.Equal(y, I("top"));
        Assert.Equal(ecranL - x - w, I("right"));
        Assert.Equal(ecranH - y - h, I("bottom"));

        // le calcul du lanceur (LibRetro.Generator.cs, ConfigureBezels) : image a la taille de l'ecran, aucune adaptation
        var vue = DeckDecor.ViewportDuLanceur(I("width"), I("height"), I("left"), I("top"), I("right"), I("bottom"), ecranL, ecranH, ecranL, ecranH);
        Assert.Equal(new DeckDecor.Trou(x, y, w, h), vue);

        // ce que le lanceur ecrit avec « -ratio custom », relu comme RetroArch 1.22 le lit : l'image tombe dans le trou
        var cfg = new Dictionary<string, string>
        {
            ["video_aspect_ratio_auto"] = "false",
            ["aspect_ratio_index"] = DeckViewport.IndexCustom.ToString(),
            ["video_viewport_bias_x"] = "0.000000",
            ["video_viewport_bias_y"] = "0.000000",
            ["custom_viewport_x"] = vue!.X.ToString(),
            ["custom_viewport_y"] = vue.Y.ToString(),
            ["custom_viewport_width"] = vue.Width.ToString(),
            ["custom_viewport_height"] = vue.Height.ToString(),
            ["video_scale_integer"] = "false",
        };
        var (rect, _) = DeckViewport.Calculer(DeckVideoSettings.Depuis(cfg), ecranL, ecranH, natifL, natifH, natifL / (double)natifH);
        Assert.Equal(new DeckRect(x, y, w, h), rect);
        Assert.NotEmpty(jeu);
    }

    [Fact]
    public void Une_image_rendue_pour_un_autre_ecran_serait_etiree_par_le_lanceur()
    {
        // Le decor 1920 x 1200 de 19XX sur un ecran 1920 x 1080 : formes voisines (ecart 0,18 < 0,2), le lanceur etire les
        // marges ; le jeu ne ferait plus 3:4. D'ou le rendu a la taille exacte de l'ecran, a chaque lancement.
        var vue = DeckDecor.ViewportDuLanceur(1920, 1200, 510, 0, 510, 0, 1920, 1200, 1920, 1080);
        Assert.Equal(new DeckDecor.Trou(510, 0, 900, 1080), vue);
        // un ecran plus etroit que 16:10 avec une image d'une autre taille : le lanceur ne pose pas le decor
        Assert.Null(DeckDecor.ViewportDuLanceur(1920, 1200, 510, 0, 510, 0, 1920, 1200, 1280, 1024));
    }

    [Fact]
    public void Les_arguments_du_decor_passent_avant_la_rom_et_un_argument_douteux_est_refuse()
    {
        var ligne = CommandsController.ArgumentsDuLanceur("-p1index 0", "fbneo", "libretro", "fbneo", @"E:\RetroBat\roms\fbneo\mslug3.zip", false,
            DeckDecor.ArgumentsDuLanceur);
        Assert.Equal("-p1index 0 -system fbneo -emulator libretro -core fbneo -retroachievements 0 -bezel nelfeplay-deck -ratio custom -integerscale 0 -rom \"E:\\RetroBat\\roms\\fbneo\\mslug3.zip\"", ligne);
        Assert.Null(CommandsController.ArgumentsDuLanceur("", "fbneo", "libretro", "fbneo", "x.zip", null, ["-bezel", "a b"]));
        // sans supplement, la ligne d'avant
        Assert.Equal("-system nes -emulator libretro -rom \"x.nes\"", CommandsController.ArgumentsDuLanceur("", "nes", "libretro", null, "x.nes", null));
    }

    [Fact]
    public void En_MULTIPLAYER_le_decor_du_Deck_demande_le_ratio_custom_et_les_panneaux_non()
    {
        var jeu = new MultiplayerGame { Id = "19xx", System = "fbneo" };
        var avecDeck = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", "19xx.zip", jeu, 1, [], decor: DeckDecor.Nom)!;
        Assert.Contains("-bezel nelfeplay-deck -ratio custom -integerscale 0", avecDeck);
        var avecPanneaux = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", "19xx.zip", jeu, 1, [], decor: "nelfeplay-multiplayer")!;
        Assert.Contains("-bezel nelfeplay-multiplayer", avecPanneaux);
        Assert.DoesNotContain("-ratio", avecPanneaux);
    }

    [Fact]
    public void La_sortie_du_rendu_du_Deck_se_lit_et_un_trou_hors_ecran_est_refuse()
    {
        const string sortie = "{\"Type\":\"deck.decor\",\"Deck\":\"metal-slug-3-pixel\",\"Name\":\"Metal Slug 3 Rebel Iron\",\"Version\":1,\"Official\":true,"
            + "\"Screen\":{\"Width\":1920,\"Height\":1200},\"Hole\":{\"X\":476,\"Y\":58,\"Width\":1444,\"Height\":1083},\"Native\":{\"Width\":304,\"Height\":224},"
            + "\"Png\":\"E:\\\\x.png\",\"Bytes\":36827,\"Ms\":40}";
        Assert.Equal(new DeckDecor.Trou(476, 58, 1444, 1083), DeckLaunchService.LireLeRendu(sortie, 1920, 1200));
        Assert.Null(DeckLaunchService.LireLeRendu(sortie, 1920, 1080));
        Assert.Null(DeckLaunchService.LireLeRendu(sortie.Replace("\"Width\":1444", "\"Width\":1500"), 1920, 1200));
        Assert.Null(DeckLaunchService.LireLeRendu("{\"Type\":\"deck.decor\",\"Error\":\"deck_not_installed\"}", 1920, 1200));
    }

    [Fact]
    public void Le_decor_s_ecrit_pour_le_jeu_et_pour_son_systeme_puis_s_efface()
    {
        var png = Path.Combine(_dossier, "rendu.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4e, 0x47]);
        var racine = Path.Combine(_dossier, "decorations", DeckDecor.Nom);
        Assert.True(DeckDecor.Ecrire("fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip", png, "{}", racine));
        Assert.True(File.Exists(Path.Combine(racine, "games", "fbneo", "19xx.png")));
        Assert.True(File.Exists(Path.Combine(racine, "games", "fbneo", "19xx.info")));
        Assert.True(File.Exists(Path.Combine(racine, "systems", "fbneo.png")));
        Assert.False(DeckDecor.Ecrire("-fbneo", "19xx.zip", png, "{}", racine));
        DeckDecor.Effacer(racine);
        Assert.False(Directory.Exists(racine));
    }

    // ─────────────────────────── Decks installes ───────────────────────────

    private string Installer(string sous, string id, string roms, bool defaut = false, string? variante = null)
    {
        var dossier = Path.Combine(_dossier, "deck", sous);
        Directory.CreateDirectory(dossier);
        var fichier = Path.Combine(dossier, id + ".json");
        File.WriteAllText(fichier, "{ \"format\": \"nelfeplay.deck-theme/1\", \"id\": \"" + id + "\", \"name\": \"" + id.ToUpperInvariant() + "\", \"version\": 2,"
            + (defaut ? " \"deck\": { \"default\": true }," : "")
            + " \"match\": { \"roms\": [" + roms + "]" + (variante is null ? "" : ", \"variantOf\": \"" + variante + "\"") + " } }");
        return fichier;
    }

    private DeckCatalogue Catalogue()
    {
        Installer("themes", "19xx", "\"19xx\", \"19xxa\"");
        Installer("themes", "19xx-hangar", "\"19xx\", \"19xxa\"", defaut: true, variante: "19xx");
        Installer("themes", "metal-slug-3-pixel", "\"mslug3\"", defaut: true, variante: "metal-slug-3");
        Installer("themes", "neutral", "");
        Installer("decks", "19xx-mine", "\"19xx\"");
        // un Deck de joueur qui reprend l'identifiant d'un officiel est ignore
        Installer("decks", "19xx", "\"sonic\"");
        return DeckCatalogue.Lire(Path.Combine(_dossier, "deck"));
    }

    [Fact]
    public void Les_Decks_d_un_jeu_viennent_dans_l_ordre_du_choix_et_l_identifiant_d_un_officiel_reste_le_sien()
    {
        var c = Catalogue();
        Assert.Equal(["19xx-hangar", "19xx", "19xx-mine"], c.PourLeJeu("19xx", null).Select(e => e.Id).ToArray());
        Assert.True(c.Trouver("19xx")!.Official);
        Assert.Equal(["19xx"], c.Trouver("19xx")!.Roms.Where(r => r == "sonic" || r == "19xx").ToArray());
        Assert.False(c.Trouver("19xx-mine")!.Official);
        Assert.False(c.Trouver("19xx-mine")!.Default);
        Assert.Equal(2, c.Trouver("19xx-hangar")!.Version);
        Assert.Empty(c.PourLeJeu("sonic", null));
        Assert.Equal(["19xx-hangar", "19xx", "19xx-mine"], c.PourLeJeu("19xx-the-war-against-destiny", "19xx").Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Les_Decks_officiels_livres_marquent_Hangar_et_Rebel_Iron_par_defaut()
    {
        // Les vrais themes du depot du PlayerDeck, quand il est la (borne de dev)
        var depot = @"E:\RetroBat\plugins\NelfePlay-PlayerDeck";
        if (!Directory.Exists(Path.Combine(depot, "themes"))) return;
        var c = DeckCatalogue.Lire(depot);
        Assert.Equal("19xx-hangar", c.PourLeJeu("19xx", null).First().Id);
        Assert.True(c.PourLeJeu("19xx", null).First().Default);
        Assert.Equal("metal-slug-3-pixel", c.PourLeJeu("mslug3", null).First().Id);
        Assert.Contains(c.PourLeJeu("mslug3", null), e => e.Id == "metal-slug-3");
    }

    // ─────────────────────────── le choix, par joueur, jeu et mode ───────────────────────────

    [Fact]
    public void Le_choix_se_resout_par_mode_puis_par_jeu_puis_par_le_Deck_officiel()
    {
        var installes = Catalogue().PourLeJeu("19xx", null);
        var choix = new List<DeckChoix.Entree>();
        DeckChoix.Decision R(string mode) => DeckChoix.Resoudre(choix, "nelfe", "arcade/19xx", mode, installes);

        Assert.Equal(new DeckChoix.Decision("19xx-hangar", "official-default"), R(DeckModes.WorldScoring));

        choix.Add(new DeckChoix.Entree("nelfe", "arcade/19xx", DeckModes.Speedrun, "19xx", DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(new DeckChoix.Decision("19xx", "choice"), R(DeckModes.Speedrun));
        // un autre mode reprend le dernier choix du joueur pour ce jeu
        Assert.Equal(new DeckChoix.Decision("19xx", "choice-other-mode"), R(DeckModes.WorldScoring));

        choix.Add(new DeckChoix.Entree("nelfe", "arcade/19xx", DeckModes.WorldScoring, DeckChoix.Off, DateTime.UtcNow));
        Assert.Equal(new DeckChoix.Decision(null, "choice"), R(DeckModes.WorldScoring));
        Assert.Equal(new DeckChoix.Decision("19xx", "choice"), R(DeckModes.Speedrun));
        Assert.Equal(new DeckChoix.Decision(null, "choice-other-mode"), R(DeckModes.Multiplayer));

        // un autre joueur part du Deck officiel ; un Deck desinstalle ne compte plus
        Assert.Equal(new DeckChoix.Decision("19xx-hangar", "official-default"), DeckChoix.Resoudre(choix, "invite", "arcade/19xx", DeckModes.Speedrun, installes));
        var sansSteelSky = installes.Where(e => e.Id != "19xx").ToList();
        Assert.Equal(new DeckChoix.Decision(null, "choice-other-mode"), DeckChoix.Resoudre(choix, "nelfe", "arcade/19xx", DeckModes.Speedrun, sansSteelSky));

        // un jeu sans Deck officiel et sans choix : rien d'office (le Deck d'un joueur n'est jamais pris d'office)
        var seulement = installes.Where(e => !e.Official).ToList();
        Assert.Equal(new DeckChoix.Decision(null, "none"), DeckChoix.Resoudre([], "nelfe", "arcade/19xx", DeckModes.Speedrun, seulement));
    }

    [Fact]
    public void Le_choix_suivant_fait_le_tour_des_Decks_puis_Off()
    {
        var installes = Catalogue().PourLeJeu("19xx", null);
        Assert.Equal("19xx", DeckChoix.Suivant("19xx-hangar", installes));
        Assert.Equal("19xx-mine", DeckChoix.Suivant("19xx", installes));
        Assert.Equal(DeckChoix.Off, DeckChoix.Suivant("19xx-mine", installes));
        Assert.Equal("19xx-hangar", DeckChoix.Suivant(DeckChoix.Off, installes));
        Assert.Equal("19xx-hangar", DeckChoix.Suivant("inconnu", installes));
    }

    [Fact]
    public void Le_choix_est_garde_sur_le_disque()
    {
        var fichier = Path.Combine(_dossier, "choix.json");
        new DeckChoix(fichier).Poser("nelfe", "arcade/19xx", DeckModes.WorldScoring, DeckChoix.Off);
        var relu = new DeckChoix(fichier);
        Assert.Single(relu.Tous());
        Assert.Equal(DeckChoix.Off, relu.Tous()[0].Deck);
        Assert.Contains(DeckChoix.Schema, File.ReadAllText(fichier));
    }

    // ─────────────────────────── le geste ───────────────────────────

    [Fact]
    public void Select_puis_A_court_choisit_et_le_maintien_ou_A_seul_ne_choisit_pas()
    {
        var g = new DeckGeste();
        var t = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
        Assert.False(g.Entree("select", 1, true, t));
        Assert.False(g.Entree("a", 1, true, t.AddMilliseconds(100)));
        Assert.True(g.Entree("a", 1, false, t.AddMilliseconds(300)));

        // A maintenu une seconde (la relance du speedrun, E.6) : rien pour le Deck
        Assert.False(g.Entree("a", 1, true, t.AddMilliseconds(400)));
        Assert.False(g.Entree("a", 1, false, t.AddMilliseconds(1400)));
        Assert.False(g.Entree("select", 1, false, t.AddMilliseconds(1500)));

        // A seul (le tir ou la bombe du jeu), ou A avant Select
        Assert.False(g.Entree("a", 1, true, t.AddMilliseconds(2000)));
        Assert.False(g.Entree("select", 1, true, t.AddMilliseconds(2050)));
        Assert.False(g.Entree("a", 1, false, t.AddMilliseconds(2100)));

        // le joueur 2 ne choisit pas le Deck du joueur local
        Assert.False(g.Entree("select", 2, true, t.AddMilliseconds(3000)));
        Assert.False(g.Entree("a", 2, true, t.AddMilliseconds(3050)));
        Assert.False(g.Entree("a", 2, false, t.AddMilliseconds(3100)));
    }

    /// <summary>Les raccourcis de RetroArch de la borne (retroarch.cfg du 08/11, extrait).</summary>
    private static readonly string[] RaccourcisDeLaBorne =
    [
        "input_ai_service_btn = \"10\"", "input_disk_eject_toggle_btn = \"9\"", "input_enable_hotkey_btn = \"4\"", "input_exit_emulator_btn = \"6\"",
        "input_hold_fast_forward_btn = \"14\"", "input_load_state_btn = \"3\"", "input_menu_toggle_btn = \"0\"", "input_pause_toggle_btn = \"7\"",
        "input_rewind_btn = \"13\"", "input_save_state_btn = \"2\"", "input_screenshot_btn = \"8\"", "input_state_slot_decrease_btn = \"12\"",
        "input_state_slot_increase_btn = \"11\"", "input_overlay_next_btn = \"nul\"",
        "input_player1_a_btn = \"1\"", "input_player1_b_btn = \"0\"", "input_player1_l_btn = \"9\"", "input_player1_select_btn = \"4\"",
    ];

    [Fact]
    public void Select_plus_A_est_libre_dans_le_retroarch_cfg_de_la_borne_et_L1_ne_l_est_pas()
    {
        Assert.True(DeckGeste.CombinaisonLibre(RaccourcisDeLaBorne, out var raison), raison);
        Assert.False(DeckGeste.CombinaisonLibre(RaccourcisDeLaBorne.Append("input_player1_a_btn = \"9\""), out raison));
        Assert.Equal("a_is_hotkey:input_disk_eject_toggle_btn", raison);
        Assert.False(DeckGeste.CombinaisonLibre(RaccourcisDeLaBorne.Append("input_enable_hotkey_btn = \"5\""), out raison));
        Assert.Equal("hotkey_not_select", raison);
    }

    // ─────────────────────────── le lancement ───────────────────────────

    private DeckLaunchService Service(SimpleEventBus bus, ApiContext contexte, DeckStateService? etat = null, int largeur = 1920, int hauteur = 1200)
    {
        var catalogue = Catalogue();
        var png = Path.Combine(_dossier, "rendu.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4e, 0x47]);
        return new DeckLaunchService(bus, contexte, etat, fichierDesChoix: Path.Combine(_dossier, "choix.json"), commandes: new CommandesMuettes())
        {
            Catalogue = () => catalogue,
            Ecran = () => (largeur, hauteur),
            RacineDuDecor = Path.Combine(_dossier, "decorations", DeckDecor.Nom),
            DemarrerLeHost = false,
            // 19XX centre en 3:4, comme le rendu du Deck
            Rendeur = (e, l, h) => new DeckRendu(png, new DeckDecor.Trou((l - h * 3 / 4) / 2, 0, h * 3 / 4, h), l, h, false, 1),
        };
    }

    private sealed class CommandesMuettes : ICommandesRetroArch
    {
        public readonly List<string> Envoyees = new();
        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500) => Task.FromResult<string?>(null);
        public Task EnvoyerAsync(string commande, CancellationToken ct) { Envoyees.Add(commande); return Task.CompletedTask; }
    }

    [Fact]
    public void Un_jeu_qui_a_un_Deck_officiel_part_avec_son_decor_et_un_jeu_sans_Deck_part_comme_avant()
    {
        var bus = new SimpleEventBus();
        var publies = new List<EventEnvelope>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type?.StartsWith("deck.") == true) publies.Add(e); });
        var service = Service(bus, new ApiContext { PlayerPseudo = "nelfe" });

        var l = service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip");
        Assert.Equal("posed", l.Status);
        Assert.Equal("19xx-hangar", l.DeckId);
        Assert.Equal("official-default", l.Source);
        Assert.Equal(DeckDecor.ArgumentsDuLanceur, l.Arguments);
        Assert.Equal(new DeckDecor.Trou(510, 0, 900, 1200), l.Decor!.Hole);
        Assert.Equal(l.Decor.Hole, l.Decor.LauncherViewport);
        Assert.Equal(["19xx-hangar", "19xx", "19xx-mine", "off"], l.Choices);
        var info = File.ReadAllText(Path.Combine(_dossier, "decorations", DeckDecor.Nom, "games", "fbneo", "19xx.info"));
        Assert.Equal(510, JsonDocument.Parse(info).RootElement.GetProperty("right").GetInt32());
        Assert.Contains(publies, e => e.Type == "deck.launch");

        var sans = service.Preparer(DeckModes.WorldScoring, "megadrive", @"E:\RetroBat\roms\megadrive\sonic.zip");
        Assert.Equal("none", sans.Status);
        Assert.Empty(sans.Arguments);
        Assert.Null(service.EnCours);

        // Adventure est prevu, non branche
        Assert.Empty(service.Preparer(DeckModes.Adventure, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip").Arguments);
    }

    [Fact]
    public void Le_choix_en_jeu_fait_le_tour_et_Off_retire_le_decor_au_lancement_suivant()
    {
        var bus = new SimpleEventBus();
        var choix = new List<DeckChoixPublie>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.choice") choix.Add((DeckChoixPublie)e.Payload!); });
        var contexte = new ApiContext { PlayerPseudo = "nelfe" };
        var service = Service(bus, contexte);

        // hors d'un lancement des boutons speciaux, le choix ne fait rien
        Assert.Null(service.Choisir(null, "gamepad"));

        service.Preparer(DeckModes.Speedrun, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip");
        var c1 = service.Choisir(null, "gamepad")!;
        Assert.Equal("19xx", c1.DeckId);
        Assert.Equal("next-launch", c1.Now);
        Assert.Equal("PlayerDeck: 19XX from next launch", c1.Label);
        Assert.Equal("19xx-mine", service.Choisir(null, "keyboard")!.DeckId);
        var off = service.Choisir(null, "gamepad")!;
        Assert.True(off.Off);
        Assert.Equal("hide", off.Now);
        Assert.Equal("PlayerDeck off (frame stays until next launch)", off.Label);
        Assert.Equal("show", service.Choisir("19xx-hangar", "keyboard")!.Now);
        Assert.Null(service.Choisir("sonic", "keyboard"));
        Assert.Equal(4, choix.Count);

        // Off retenu pour ce joueur, ce jeu et ce mode : le lancement suivant part sans decor, le choix en jeu reste possible
        service.Choisir(DeckChoix.Off, "keyboard");
        var suivant = service.Preparer(DeckModes.Speedrun, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip");
        Assert.Equal("off", suivant.Status);
        Assert.Empty(suivant.Arguments);
        Assert.NotNull(service.EnCours);
        var c = service.Choisir(null, "gamepad")!;
        Assert.Equal("19xx-hangar", c.DeckId);
        Assert.Equal("switch", c.Now);

        // un autre mode reprend ce choix ; un autre joueur garde le Deck officiel
        Assert.Equal("posed", service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip").Status);
        contexte.SessionPseudo = "invite";
        Assert.Equal("19xx-hangar", service.Preparer(DeckModes.Speedrun, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip").DeckId);
    }

    [Fact]
    public void Le_geste_lu_par_l_API_change_le_Deck_pendant_une_partie_lancee_avec_lui()
    {
        var bus = new SimpleEventBus();
        var choix = new List<DeckChoixPublie>();
        bus.Subscribe<EventEnvelope>(e => { if (e.Type == "deck.choice") choix.Add((DeckChoixPublie)e.Payload!); });
        var contexte = new ApiContext { PlayerPseudo = "nelfe" };
        var service = Service(bus, contexte);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            service.Preparer(DeckModes.WorldScoring, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip");
            contexte.Ui.Running = new GameReference { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\19xx.zip" };
            void Appui(string id, bool bas) => bus.PublishAsync(new EventEnvelope { Type = bas ? "panel.input.pressed" : "panel.input.released", Payload = new { Player = 1, Identity = id, Device = 0 } });
            Appui("select", true);
            Appui("a", true);
            Appui("a", false);
            Appui("select", false);
            Assert.Single(choix);
            Assert.Equal("19xx", choix[0].DeckId);
            Assert.Equal("gamepad", choix[0].Source);
        }
        finally
        {
            service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Le_Deck_prepare_se_rattache_a_la_partie_du_meme_jeu_et_pas_a_une_autre()
    {
        var bus = new SimpleEventBus();
        var contexte = new ApiContext { PlayerPseudo = "nelfe" };
        var etat = new DeckStateService(bus, contexte);
        etat.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var service = Service(bus, contexte, etat);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            service.Preparer(DeckModes.Speedrun, "fbneo", @"E:\RetroBat\roms\fbneo\19xx.zip");
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\19xx.zip" } });
            var jeu = etat.Snapshot("test").Game!;
            Assert.Equal("posed", jeu.Deck!.Status);
            Assert.Equal("19xx-hangar", jeu.Deck.DeckId);
            Assert.Contains("\"Deck\":{", JsonSerializer.Serialize(jeu));
            Assert.DoesNotContain("Arguments", JsonSerializer.Serialize(jeu));

            // l'accuse du choix passe par le WebSocket du Deck (Ctrl+Alt+D)
            using var vide = JsonDocument.Parse("{}");
            var accuse = etat.Commande(new object(), "deck.choice.next", vide.RootElement)!;
            Assert.Contains("\"Accepted\":true", accuse);

            bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            // un jeu lance ensuite par ES n'a pas de Deck (H-D20)
            bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\1942.zip" } });
            Assert.Null(etat.Snapshot("test").Game!.Deck);
        }
        finally
        {
            service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            etat.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Le_cadre_du_decor_est_le_plan_et_son_sort_se_dit_sans_la_voie_A()
    {
        var plan = new DeckFramePlan { X = 476, Y = 58, Width = 1444, Height = 1083, ScreenWidth = 1920, ScreenHeight = 1200, Theme = "metal-slug-3-pixel" };
        var fenetre = new DeckRect(0, 0, 1920, 1200);
        Assert.Equal("applied", DeckStateService.PlanDe(plan, new DeckRect(476, 58, 1444, 1083), fenetre, null)!.Status);
        Assert.Equal("decor_not_posed", DeckStateService.PlanDe(plan, new DeckRect(260, 0, 1600, 1200), fenetre, null)!.Reason);
        Assert.Equal("screen_mismatch", DeckStateService.PlanDe(plan, new DeckRect(240, 0, 1440, 1080), new DeckRect(0, 0, 1920, 1080), null)!.Reason);
    }
}
