using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les reglages qu'une partie certifiee doit prouver : seulement ceux qui changent le jeu, et la
/// NVRAM du jeu quel que soit l'emulateur qui l'a ecrite.
/// </summary>
public class CertifiedSettingsTests
{
    /// <summary>Les options FBNeo reelles d'une partie 19xx sur la borne (2026-09-14).</summary>
    private const string Options19xx =
        "fbneo-allow-depth-32=enabled;fbneo-allow-patched-romsets=disabled;fbneo-analog-speed=100%;fbneo-cpu-speed-adjust=100%;"
        + "fbneo-diagnostic-input=Start + L + R;fbneo-fixed-frameskip=0;fbneo-fm-interpolation=4-point 3rd order;fbneo-force-60hz=disabled;"
        + "fbneo-frameskip-manual-threshold=33;fbneo-frameskip-type=disabled;fbneo-hiscores=enabled;"
        + "fbneo-lightgun-crosshair-emulation=hide with lightgun device;fbneo-lowpass-filter=disabled;fbneo-resolution=640x480;"
        + "fbneo-sample-interpolation=4-point 3rd order;fbneo-samplerate=44100;fbneo-socd=3;fbneo-vertical-mode=disabled";

    [Fact]
    public void FBNeo_ne_garde_que_les_reglages_de_jeu()
    {
        Assert.Equal(
            "fbneo-allow-patched-romsets=disabled;fbneo-analog-speed=100%;fbneo-cpu-speed-adjust=100%;fbneo-force-60hz=disabled;fbneo-socd=3",
            NelfePlayScoringReporter.FilterGameplayCoreOptions(Options19xx));
    }

    [Fact]
    public void Changer_la_resolution_ou_le_diagnostic_ne_change_rien()
    {
        var modifie = Options19xx.Replace("fbneo-resolution=640x480", "fbneo-resolution=1280x960")
            .Replace("fbneo-diagnostic-input=Start + L + R", "fbneo-diagnostic-input=Hold Start");
        Assert.Equal(NelfePlayScoringReporter.FilterGameplayCoreOptions(Options19xx), NelfePlayScoringReporter.FilterGameplayCoreOptions(modifie));
    }

    [Fact]
    public void Les_DIP_et_les_cheats_de_FBNeo_restent_controles()
    {
        var filtre = NelfePlayScoringReporter.FilterGameplayCoreOptions(
            "fbneo-resolution=640x480;fbneo-dipswitch-1942-Difficulty=Hard;fbneo-cheat-1942-Infinite_lives=enabled");
        Assert.Equal("fbneo-cheat-1942-Infinite_lives=enabled;fbneo-dipswitch-1942-Difficulty=Hard", filtre);
    }

    /// <summary>Les options du coeur MAME de RetroArch telles que RetroBat les ecrit sur la borne (2026-09-17).</summary>
    private const string OptionsMameLibretro =
        "mame_alternate_renderer=disabled;mame_altres=640x480;mame_auto_save=disabled;mame_autoloadfastforward=disabled;"
        + "mame_boot_to_bios=disabled;mame_boot_to_osd=disabled;mame_buttons_profiles=disabled;mame_cheats_enable=disabled;"
        + "mame_coin_limit=0;mame_cpu_overclock=default;mame_cpu_sound_overclock=default;mame_current_aspect_ratio=DAR;"
        + "mame_current_frame_skip=0;mame_current_sample_rate=44100Hz;mame_current_turbo_button=disabled;mame_current_turbo_delay=medium;"
        + "mame_joystick_deadzone=0.15;mame_lightgun_mode=lightgun;mame_mouse_enable=enabled;mame_read_config=disabled;"
        + "mame_rotation_mode=internal;mame_thread_mode=enabled;mame_throttle=disabled";

    [Fact]
    public void MAME_RetroArch_ne_garde_que_les_reglages_de_jeu()
    {
        Assert.Equal(
            "mame_auto_save=disabled;mame_cheats_enable=disabled;mame_cpu_overclock=default;mame_cpu_sound_overclock=default;"
            + "mame_current_turbo_button=disabled;mame_read_config=disabled",
            NelfePlayScoringReporter.FilterGameplayCoreOptions(OptionsMameLibretro));
    }

    [Fact]
    public void MAME_RetroArch_ecran_et_manettes_ne_changent_rien()
    {
        var autreBorne = OptionsMameLibretro.Replace("mame_altres=640x480", "mame_altres=1920x1080")
            .Replace("mame_current_aspect_ratio=DAR", "mame_current_aspect_ratio=PAR")
            .Replace("mame_joystick_deadzone=0.15", "mame_joystick_deadzone=0.25")
            .Replace("mame_mouse_enable=enabled", "mame_mouse_enable=disabled")
            .Replace("mame_lightgun_mode=lightgun", "mame_lightgun_mode=touchscreen");
        Assert.Equal(NelfePlayScoringReporter.FilterGameplayCoreOptions(OptionsMameLibretro), NelfePlayScoringReporter.FilterGameplayCoreOptions(autreBorne));
    }

    [Fact]
    public void MAME_RetroArch_cheats_et_overclock_restent_controles()
    {
        Assert.NotEqual(NelfePlayScoringReporter.FilterGameplayCoreOptions(OptionsMameLibretro),
            NelfePlayScoringReporter.FilterGameplayCoreOptions(OptionsMameLibretro.Replace("mame_cheats_enable=disabled", "mame_cheats_enable=enabled")));
        Assert.NotEqual(NelfePlayScoringReporter.FilterGameplayCoreOptions(OptionsMameLibretro),
            NelfePlayScoringReporter.FilterGameplayCoreOptions(OptionsMameLibretro.Replace("mame_cpu_overclock=default", "mame_cpu_overclock=150")));
    }

    [Fact]
    public void Les_DIP_MAME_passent_inchanges()
    {
        Assert.Equal("1-1=1;Difficulty=Normal", NelfePlayScoringReporter.FilterGameplayCoreOptions("Difficulty=Normal;1-1=1"));
    }

    [Fact]
    public void La_NVRAM_de_MAME_autonome_est_trouvee_quel_que_soit_le_systeme()
    {
        var saves = Path.Combine(Path.GetTempPath(), "nvram-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(saves, "fbneo", "fbneo"));
            Directory.CreateDirectory(Path.Combine(saves, "mame", "nvram", "19xx"));
            File.WriteAllBytes(Path.Combine(saves, "fbneo", "fbneo", "19xx.nv"), new byte[128]);
            File.WriteAllBytes(Path.Combine(saves, "mame", "nvram", "19xx", "eeprom"), new byte[128]);
            File.WriteAllBytes(Path.Combine(saves, "fbneo", "fbneo", "19xx.hi"), new byte[16]);   // pas une NVRAM

            var trouves = NvramSnapshotService.Chercher("fbneo", "19xx", saves)
                .Select(c => Path.GetRelativePath(saves, c).Replace('\\', '/')).OrderBy(c => c).ToArray();
            Assert.Equal(new[] { "fbneo/fbneo/19xx.nv", "mame/nvram/19xx/eeprom" }, trouves);

            Assert.Equal(new[] { "mame/nvram/19xx/eeprom" },
                NvramSnapshotService.Chercher("arcade", "19xx", saves).Select(c => Path.GetRelativePath(saves, c).Replace('\\', '/')).ToArray());
            Assert.Empty(NvramSnapshotService.Chercher("mame", "..", saves));
        }
        finally
        {
            try { Directory.Delete(saves, recursive: true); } catch { }
        }
    }
}
