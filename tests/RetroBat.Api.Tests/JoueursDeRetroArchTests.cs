using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le joueur 1 de l'API est celui que RetroArch a recu, pas celui qu'ES a mis en p1 (borne du user, 13/11) : panel
/// branche apres coup, ES le met en p1 (index SDL 1) et la Xbox en p2 (index 0), le lanceur ecrit
/// input_player1_joypad_index = 0 : dans le jeu, la Xbox est le joueur 1.
/// </summary>
public class JoueursDeRetroArchTests
{
    // La ligne reelle du journal du lanceur, 13/11 21:34 (19XX, lancement de l'API).
    private const string LigneDu13 =
        "2026-11-13 21:34:12.657 [INFO]      [Startup] \"E:\\RetroBat\\emulationstation\\emulatorLauncher.exe\" -p1index 1 -p1guid 03000000790000000600000000000000 -p1path \"USB\\VID_0079&PID_0006\\8&3A944CE5&0&1\" -p1name \"Generic USB Joystick\" -p1nbbuttons 12 -p1nbhats 1 -p1nbaxes 5 -p2index 0 -p2guid 030000005e040000fd02000003097200 -p2path \"USB\\VID_18D1&PID_9400&MI_01\\8&276CE642&0&0001\" -p2name \"Xbox One S Controller\" -p2nbbuttons 16 -p2nbhats 1 -p2nbaxes 6 -system fbneo -emulator libretro -core fbneo -bezel nelfeplay-deck -rom \"E:\\RetroBat\\roms\\fbneo\\19xx.zip\"";

    // Ce que le lanceur a ecrit dans retroarch.cfg pour ce lancement (extraits reels).
    private static readonly string[] ConfigDu13 =
    {
        "input_joypad_driver = \"sdl2\"",
        "input_player1_a_btn = \"1\"",
        "input_player1_joypad_index = \"0\"",
        "input_player2_joypad_index = \"1\"",
        "input_player3_joypad_index = \"2\"",
        "input_player16_joypad_index = \"15\"",
    };

    // Les appareils tels que le lecteur de l'API les voit (SDL_JOYSTICK_RAWINPUT=0 : la Xbox en XInput, « 7801 »).
    private static readonly PlayerNumbering.Device XboxApi = new("030000005e040000fd02000003097801", "Xbox One S Controller");
    private static readonly PlayerNumbering.Device PanelApi = new("03000000790000000600000000000000", "Generic USB Joystick");
    private static readonly IReadOnlyDictionary<int, string> SansEpingle = new Dictionary<int, string>();

    [Fact]
    public void Lit_les_manettes_de_la_ligne_du_lanceur()
    {
        var manettes = JoueursDeRetroArch.ManettesDuLancement(LigneDu13);
        Assert.Equal(2, manettes.Count);
        Assert.Equal(new JoueursDeRetroArch.Manette(1, 1, "03000000790000000600000000000000", "Generic USB Joystick"), manettes[0]);
        Assert.Equal(new JoueursDeRetroArch.Manette(2, 0, "030000005e040000fd02000003097200", "Xbox One S Controller"), manettes[1]);
    }

    [Fact]
    public void Lit_le_pilote_et_les_index_de_retroarch_cfg()
    {
        var (pilote, index) = JoueursDeRetroArch.LireConfig(ConfigDu13);
        Assert.Equal("sdl2", pilote);
        Assert.Equal(0, index[1]);
        Assert.Equal(1, index[2]);
        Assert.Equal(15, index[16]);
    }

    [Fact]
    public void Panel_branche_apres_coup_ES_le_met_en_p1_RetroArch_garde_la_Xbox_au_port_1()
    {
        var (pilote, index) = JoueursDeRetroArch.LireConfig(ConfigDu13);
        var attribution = JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(LigneDu13), pilote, index);

        Assert.NotNull(attribution);
        Assert.Equal("Xbox One S Controller", attribution!.ParJoueur[1].Nom);
        Assert.Equal("Generic USB Joystick", attribution.ParJoueur[2].Nom);
        Assert.True(attribution.DiffereDEs);

        // L'API suit RetroArch pendant la partie : la Xbox est son joueur 1, quel que soit l'ordre de SDL chez elle.
        Assert.Equal(new[] { 1, 2 }, PlayerNumbering.Assign(new[] { XboxApi, PanelApi }, SansEpingle, attribution.Guids));
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { PanelApi, XboxApi }, SansEpingle, attribution.Guids));
    }

    [Fact]
    public void Hors_partie_la_regle_du_panel_devant_reste()
    {
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { XboxApi, PanelApi }, SansEpingle, null));
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { XboxApi, PanelApi }, SansEpingle));
    }

    [Fact]
    public void Lanceur_qui_suit_ES_le_panel_reste_joueur_1()
    {
        // Le cas sain : ES met le panel en p1 (index 0) et le lanceur lui donne le port 1.
        var ligne = "2026-11-13 22:00:00.000 [INFO] [Startup] \"emulatorLauncher.exe\" -p1index 0 -p1guid 03000000790000000600000000000000 -p1name \"Generic USB Joystick\" -p2index 1 -p2guid 030000005e040000fd02000003097200 -p2name \"Xbox One S Controller\" -system fbneo";
        var attribution = JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(ligne), "sdl2",
            new Dictionary<int, int> { [1] = 0, [2] = 1 });

        Assert.NotNull(attribution);
        Assert.False(attribution!.DiffereDEs);
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { XboxApi, PanelApi }, SansEpingle, attribution.Guids));
    }

    [Fact]
    public void Manette_seule_au_lancement_le_panel_passe_derriere()
    {
        // Le lancement de 20:29 (calcul d'ES trop lent, repli sur la ligne d'ES) : la Xbox seule, au port 1.
        var ligne = "2026-11-13 20:29:00.908 [INFO] [Startup] \"emulatorLauncher.exe\" -p1index 0 -p1guid 030000005e040000fd02000003097200 -p1name \"Xbox One S Controller\" -system fbneo";
        var attribution = JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(ligne), "sdl2",
            new Dictionary<int, int> { [1] = 0, [2] = 1 });

        Assert.NotNull(attribution);
        Assert.Single(attribution!.ParJoueur);
        Assert.Equal(new[] { 1, 2 }, PlayerNumbering.Assign(new[] { XboxApi, PanelApi }, SansEpingle, attribution.Guids));
    }

    [Theory]
    [InlineData("dinput")]
    [InlineData("xinput")]
    [InlineData(null)]
    public void Autre_pilote_que_sdl2_on_ne_devine_pas(string? pilote)
    {
        Assert.Null(JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(LigneDu13), pilote,
            new Dictionary<int, int> { [1] = 0, [2] = 1 }));
    }

    [Fact]
    public void Un_index_qui_ne_designe_aucune_manette_du_lancement_ne_compte_pas()
    {
        var manettes = JoueursDeRetroArch.ManettesDuLancement(LigneDu13);
        Assert.Null(JoueursDeRetroArch.Resoudre(manettes, "sdl2", new Dictionary<int, int> { [1] = 5, [2] = 7 }));
        Assert.Null(JoueursDeRetroArch.Resoudre(Array.Empty<JoueursDeRetroArch.Manette>(), "sdl2", new Dictionary<int, int> { [1] = 0 }));

        var partielle = JoueursDeRetroArch.Resoudre(manettes, "sdl2", new Dictionary<int, int> { [1] = 0, [2] = 9 });
        Assert.Equal("Xbox One S Controller", Assert.Single(partielle!.ParJoueur).Value.Nom);
    }

    [Fact]
    public void Ce_que_RetroArch_a_recu_passe_devant_une_epingle()
    {
        var attribution = JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(LigneDu13), "sdl2",
            new Dictionary<int, int> { [1] = 0, [2] = 1 })!;
        var epingles = new Dictionary<int, string> { [1] = "030000007900000006000000" };
        Assert.Equal(new[] { 1, 2 }, PlayerNumbering.Assign(new[] { XboxApi, PanelApi }, epingles, attribution.Guids));
    }

    [Fact]
    public void Une_manette_vue_en_xinput_sans_identifiant_usb_est_retrouvee()
    {
        var xinputNu = new PlayerNumbering.Device("78696e70757401000000000000000000", "XInput Controller #1");
        var attribution = JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(LigneDu13), "sdl2",
            new Dictionary<int, int> { [1] = 0, [2] = 1 })!;
        Assert.Equal(new[] { 2, 1 }, PlayerNumbering.Assign(new[] { PanelApi, xinputNu }, SansEpingle, attribution.Guids));
    }

    [Theory]
    [InlineData("030000005e040000fd02000003097200", "030000005e040000fd02000003097801", true)]  // RawInput chez ES, XInput chez l'API
    [InlineData("0300c6515e040000fd02000003097200", "030000005e040000fd02000003097801", true)]  // CRC du nom ignore
    [InlineData("03000000790000000600000000000000", "030000005e040000fd02000003097801", false)]
    [InlineData("05000000c82d00002038000000000000", "03000000c82d00002038000000000000", false)] // Bluetooth n'est pas USB
    [InlineData("", "030000005e040000fd02000003097801", false)]
    public void Meme_appareil_par_bus_constructeur_et_produit(string a, string b, bool attendu)
        => Assert.Equal(attendu, PlayerNumbering.MemeAppareil(a, b));

    [Fact]
    public void Prend_la_ligne_de_commande_pas_la_version_du_lanceur()
    {
        // Le journal reel d'un lancement : la ligne de version suit la ligne de commande, et contient aussi « EmulatorLauncher.exe ».
        var chemin = Path.Combine(Path.GetTempPath(), "nelfe-joueurs-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(chemin, string.Join(Environment.NewLine, new[]
            {
                "2026-11-13 21:30:00.000 [INFO]      [Startup] \"E:/RetroBat/emulationstation/emulatorLauncher.exe\" -p1index 0 -p1guid 030000005e040000fd02000003097200 -p1name \"Xbox One S Controller\" -system fbneo -rom \"a.zip\"",
                LigneDu13,
                "2026-11-13 21:34:12.660 [INFO]      [Startup] Loading configuration.",
                "2026-11-13 21:34:12.756 [INFO]      [Startup] EmulatorLauncher.exe version : 10/10/2026 22:26:32",
                "2026-11-13 21:34:13.661 [INFO]      [Startup] Loading Controller configuration.",
                "2026-11-13 21:34:19.692 [INFO]      [Running] E:/RetroBat/emulators/retroarch/retroarch.exe -L core.dll",
                "",
            }));
            var ligne = JoueursDeRetroArch.DernierLancement(chemin);
            Assert.NotNull(ligne);
            Assert.Equal(2, JoueursDeRetroArch.ManettesDuLancement(ligne!).Count);
            Assert.Equal(new DateTime(2026, 11, 13, 21, 34, 12, 657), JoueursDeRetroArch.HeureDeLaLigne(ligne!));
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Theory]
    [InlineData("2026-11-13 21:34:12.756 [INFO]      [Startup] EmulatorLauncher.exe version : 10/10/2026 22:26:32", false)]
    [InlineData("2026-11-13 21:34:12.657 [INFO]      [Startup] \"E:/RetroBat/emulationstation/emulatorLauncher.exe\" -system fbneo -rom \"x.zip\"", true)]
    [InlineData("2026-11-13 21:34:12.657 [INFO]      [Startup] \"E:/RetroBat/emulationstation/emulatorLauncher.exe\"  -gameinfo \"g.xml\" -p1index 0 -system nes -rom \"x.nes\"", true)]
    [InlineData("2026-11-13 21:34:19.692 [INFO]      [Running] E:/RetroBat/emulators/retroarch/retroarch.exe -system x", false)]
    public void Reconnait_une_ligne_de_lancement(string ligne, bool attendu)
        => Assert.Equal(attendu, JoueursDeRetroArch.EstUneLigneDeLancement(ligne));

    [Fact]
    public void Lit_l_heure_d_une_ligne_du_lanceur()
    {
        Assert.Equal(new DateTime(2026, 11, 13, 21, 34, 12, 657), JoueursDeRetroArch.HeureDeLaLigne(LigneDu13));
        Assert.Null(JoueursDeRetroArch.HeureDeLaLigne("[Startup] sans heure"));
    }

    [Fact]
    public void La_description_dit_l_ecart_avec_ES()
    {
        var attribution = JoueursDeRetroArch.Resoudre(JoueursDeRetroArch.ManettesDuLancement(LigneDu13), "sdl2",
            new Dictionary<int, int> { [1] = 0, [2] = 1 })!;
        Assert.Equal("1 = Xbox One S Controller (index 0, p2 chez ES), 2 = Generic USB Joystick (index 1, p1 chez ES)", attribution.Decrire());
    }
}
