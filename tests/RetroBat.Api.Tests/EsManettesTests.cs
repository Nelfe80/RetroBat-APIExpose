using System.Text;
using System.Xml.Linq;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les manettes d'un lancement sans ES (2026-10-26) : refaites comme ES les range, au format exact de sa ligne de
/// commande. La reference est une vraie ligne d'ES (une manette Xbox 360 en USB, une Wiimote en Bluetooth).
/// </summary>
public sealed class EsManettesTests
{
    private const string Xbox = @"USB\VID_045E&PID_028E&IG_00\2&9C6DEB5&0&00";
    private const string Wiimote = @"\\?\HID#{00001124-0000-1000-8000-00805F9B34FB}_VID&0002057E_PID&0330#9&2DB7C16D&1&0000#{4D1E55B2-F16F-11CF-88CB-001111000030}";

    private const string LigneDEs =
        @"-p1index 0 -p1guid 030000005e0400008e02000000007200 -p1path ""USB\VID_045E&PID_028E&IG_00\2&9C6DEB5&0&00"" -p1name ""Xbox 360 Controller"" -p1nbbuttons 11 -p1nbhats 1 -p1nbaxes 6 " +
        @"-p2index 1 -p2guid 030000007e0500003003000000000000 -p2path ""\\?\HID#{00001124-0000-1000-8000-00805F9B34FB}_VID&0002057E_PID&0330#9&2DB7C16D&1&0000#{4D1E55B2-F16F-11CF-88CB-001111000030}"" -p2name ""Nintendo RVL-CNT-01-TR"" -p2nbbuttons 0 -p2nbhats 0 -p2nbaxes 0";

    private static readonly EsManettes.Appareil ManetteXbox = new(0, "030000005e0400008e02000000007200", "Xbox 360 Controller", Xbox, 11, 1, 6, true);
    private static readonly EsManettes.Appareil ManetteWii = new(1, "030000007e0500003003000000000000", "Nintendo RVL-CNT-01-TR", Wiimote, 0, 0, 0, false);

    private static Func<string, string> Reglages(params (string Cle, string Valeur)[] valeurs)
        => cle => valeurs.FirstOrDefault(v => v.Cle == cle).Valeur ?? string.Empty;

    [Fact]
    public void Sans_reglage_les_manettes_sortent_au_format_exact_de_la_ligne_d_ES()
    {
        // L'ordre d'entree ne compte pas : ES range les manettes libres par chemin.
        var joueurs = EsManettes.AttribuerLesJoueurs([ManetteWii, ManetteXbox], Reglages());

        Assert.Equal(EsLaunchArguments.ManettesDe(LigneDEs), EsManettes.Composer(joueurs));
    }

    [Fact]
    public void Le_chemin_choisi_dans_ES_pour_un_joueur_passe_avant_tout()
    {
        var joueurs = EsManettes.AttribuerLesJoueurs([ManetteXbox, ManetteWii], Reglages(("INPUT P1PATH", Wiimote)));

        Assert.Same(ManetteWii, joueurs[0]);
        Assert.Same(ManetteXbox, joueurs[1]);
    }

    [Fact]
    public void Le_guid_et_le_nom_choisis_rangent_aussi_la_manette()
    {
        var joueurs = EsManettes.AttribuerLesJoueurs([ManetteXbox, ManetteWii],
            Reglages(("INPUT P2NAME", "Xbox 360 Controller"), ("INPUT P2GUID", "030000005e0400008e02000000007200")));

        Assert.Same(ManetteWii, joueurs[0]);
        Assert.Same(ManetteXbox, joueurs[1]);
    }

    [Fact]
    public void Un_joueur_1_vide_recoit_le_dernier_joueur_pris()
    {
        var joueurs = EsManettes.AttribuerLesJoueurs([ManetteXbox], Reglages(("INPUT P3NAME", "Xbox 360 Controller")));

        Assert.Same(ManetteXbox, joueurs[0]);
        Assert.All(joueurs.Skip(1), j => Assert.Null(j));
    }

    [Fact]
    public void Le_tri_d_un_chemin_USB_ignore_le_numero_qui_change_au_demarrage()
    {
        Assert.Equal(@"USB\VID_045E&PID_02FF&IG_00\0000B7234380ED7E",
            EsManettes.CheminDeTri(@"USB\VID_045E&PID_02FF&IG_00\01&00&0000B7234380ED7E"));
        Assert.Equal(Wiimote, EsManettes.CheminDeTri(Wiimote));
    }

    [Fact]
    public void Seules_comptent_les_manettes_configurees_dans_ES_ou_connues_de_SDL()
    {
        var esInput = XDocument.Parse("""
            <inputList>
              <inputConfig type="keyboard" deviceName="Keyboard" deviceGUID="-1"><input name="a" type="key" id="120" value="1" /></inputConfig>
              <inputConfig type="joystick" deviceName="Xbox 360 Controller" deviceGUID="030000005e0400008e02000000007200"><input name="a" type="button" id="0" value="1" /></inputConfig>
              <inputConfig type="joystick" deviceName="Vide" deviceGUID="03000000aaaa00000000000000000000"></inputConfig>
            </inputList>
            """);
        var vide = new EsManettes.Appareil(2, "03000000aaaa00000000000000000000", "Vide", "0002@x", 4, 0, 2, true);
        var inconnue = new EsManettes.Appareil(3, "03000000bbbb00000000000000000000", "Inconnue", "0003@y", 4, 0, 2, false);
        var connueDeSdl = new EsManettes.Appareil(4, "03000000cccc00000000000000000000", "Manette SDL", "0004@z", 4, 0, 2, true);

        var retenues = EsManettes.Configurees([ManetteXbox, ManetteWii, vide, inconnue, connueDeSdl], esInput);

        // La Wiimote n'a ni fiche ni mappage ici ; une fiche sans touche ne compte pas, meme si SDL connait la manette.
        Assert.Equal([ManetteXbox, connueDeSdl], retenues);
    }

    [Fact]
    public void Une_manette_debranchee_sort_de_la_ligne_et_la_suivante_devient_joueur_1()
    {
        var reste = EsManettes.SansLesAbsentes(EsLaunchArguments.ManettesDe(LigneDEs), chemin => chemin == Xbox ? false : true);

        Assert.Equal(
            @"-p1index 1 -p1guid 030000007e0500003003000000000000 -p1path ""\\?\HID#{00001124-0000-1000-8000-00805F9B34FB}_VID&0002057E_PID&0330#9&2DB7C16D&1&0000#{4D1E55B2-F16F-11CF-88CB-001111000030}"" -p1name ""Nintendo RVL-CNT-01-TR"" -p1nbbuttons 0 -p1nbhats 0 -p1nbaxes 0",
            reste);
    }

    [Fact]
    public void Une_manette_dont_on_ne_sait_rien_reste()
    {
        var ligne = EsLaunchArguments.ManettesDe(LigneDEs);

        Assert.Equal(ligne, EsManettes.SansLesAbsentes(ligne, _ => null));
        Assert.Equal(string.Empty, EsManettes.SansLesAbsentes(ligne, _ => false));
        Assert.Equal(string.Empty, EsManettes.SansLesAbsentes(string.Empty, _ => true));
    }

    [Theory]
    [InlineData(@"USB\VID_045E&PID_028E&IG_00\2&9C6DEB5&0&00", @"USB\VID_045E&PID_028E&IG_00\2&9C6DEB5&0&00")]
    [InlineData(@"USB\VID_0079&PID_0006\5&1A2B3C&0&2#COL02", @"USB\VID_0079&PID_0006\5&1A2B3C&0&2")]
    [InlineData(Wiimote, @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002057E_PID&0330\9&2DB7C16D&1&0000")]
    [InlineData("0000@030000005e0400008e02000000007200", "")]
    public void L_instance_Windows_d_un_chemin_d_ES(string chemin, string instance)
    {
        Assert.Equal(instance, PeripheriquesWindows.Instance(chemin));
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_0079&PID_0006&Col02#7&2b5c3a1&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", "#COL02")]
    [InlineData(@"\\?\HID#VID_045E&PID_028E&IG_00#3&106bdc0b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", "")]
    public void La_collection_HID_d_un_boitier_a_plusieurs_joueurs(string chemin, string suffixe)
    {
        Assert.Equal(suffixe, PeripheriquesWindows.SuffixeDeCollection(chemin));
    }
}

/// <summary>Le journal du lanceur : ses seules lignes venues d'ES comptent (pas ses en-tetes, pas les lancements de l'API).</summary>
public sealed class EsLaunchArgumentsJournalDuLanceurTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), "emulatorLauncher_" + Guid.NewGuid().ToString("N") + ".log");

    public void Dispose()
    {
        if (File.Exists(_chemin))
        {
            File.Delete(_chemin);
        }
    }

    [Fact]
    public void Seules_les_lignes_d_ES_sont_des_lancements()
    {
        const string DEs = @"2026-10-26 09:22:05.740 [INFO]      [Startup] ""C:\RetroBat\emulationstation\emulatorLauncher.exe""  -gameinfo ""C:\Temp\game.xml"" -p1index 0 -p1name ""Xbox 360 Controller""  -system mame -emulator libretro -core mame -rom ""C:\RetroBat\roms\mame\blazstar.zip""";
        var journal = string.Join("\r\n",
            @"2026-10-26 09:20:31.168 [INFO]      [Startup] C:\RetroBat\emulationstation\.emulationstation\scripts\start\..\..\..\emulatorLauncher.exe  -updatestores",
            DEs,
            "2026-10-26 09:22:05.774 [INFO]      [Startup] EmulatorLauncher.exe version : 25/10/2026 22:19:46",
            @"2026-10-26 09:24:58.407 [INFO]      [Startup] ""C:\RetroBat\emulationstation\emulatorLauncher.exe"" -system mame -emulator libretro -core fbneo -netplay true -rom ""C:\RetroBat\roms\mame\blazstar.zip""",
            "");
        File.WriteAllText(_chemin, journal, new UTF8Encoding(false));

        var lignes = EsLaunchArguments.LancementsDepuisLaFin(_chemin, filtre: EsLaunchArguments.LancementDEsDansLeJournalDuLanceur).ToList();

        Assert.Equal([DEs], lignes);
        Assert.Equal("-p1index 0 -p1name \"Xbox 360 Controller\"", EsLaunchArguments.ManettesDe(lignes[0]));
    }
}
