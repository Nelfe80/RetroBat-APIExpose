using System.Diagnostics;
using System.Text.Json;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using RetroBat.Api.Replay.Recording;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA LECTURE D'UN REPLAY DE DEFI A VERROUS (D18) : le manifeste garde les verrous attestes par le wrapper, la lecture et le
/// rendu passent alors par le wrapper de core_proxy avec ces verrous et « pipe=off ». Sans verrou, rien ne change.
/// </summary>
public class LectureAvecVerrousTests : IDisposable
{
    private const string Couteau = "0x100596,1,set_bits,0x01";
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "lecture-verrous-" + Guid.NewGuid().ToString("N"));

    public LectureAvecVerrousTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, true); } catch (IOException) { }
    }

    private static ReplayManifest Manifeste(string? verrous) => new(
        ReplayManifest.SchemaId, "rp_1", "sess_1", new ReplayGame("arcade/mslug3", "arcade", "metal-slug-3", null, "f71f8c4c"),
        DateTime.UtcNow, "home",
        new ReplayRuntime("nelfe-arcade-r1", "1.22.2", null, null, null, null, "bsv", "fbneo", null, verrous),
        new ReplayObjectRef(new string('a', 64), 10), new ReplayFrames(0, null, null, 100, 59.185606), null, new ReplayRecovery(false));

    [Fact]
    public void Sans_verrou_la_lecture_garde_le_vrai_coeur_et_n_ecrit_rien()
    {
        var psi = new ProcessStartInfo();
        var certifie = Path.Combine(_dossier, "certified.txt");
        var (coeur, erreur) = LectureAvecVerrous.Preparer(Manifeste(null), @"E:\cores\fbneo_libretro.dll", @"E:\roms\mslug3.zip", psi,
            _dossier, certifie);

        Assert.Equal(ReplayErrorCode.None, erreur);
        Assert.Equal(@"E:\cores\fbneo_libretro.dll", coeur);
        Assert.False(File.Exists(certifie));
        Assert.False(psi.Environment.ContainsKey("RETROBAT_LIBRETRO_REAL_CORE"));
    }

    [Fact]
    public void Un_replay_a_verrous_sans_wrapper_capable_n_est_pas_lu()
    {
        // Un fichier sans numero de version : un wrapper d'avant 0.343 ne dirait pas mieux.
        File.WriteAllText(Path.Combine(_dossier, "fbneo_libretro.dll"), "pas un wrapper");
        var certifie = Path.Combine(_dossier, "certified.txt");
        var (_, erreur) = LectureAvecVerrous.Preparer(Manifeste(Couteau), @"E:\cores\fbneo_libretro.dll", @"E:\roms\mslug3.zip",
            new ProcessStartInfo(), _dossier, certifie);

        Assert.Equal(ReplayErrorCode.RuntimeIncompatible, erreur);
        Assert.False(File.Exists(certifie));
    }

    [Fact]
    public void Les_lignes_du_manifeste_se_lisent_une_a_une()
    {
        Assert.Empty(LectureAvecVerrous.Lignes(Manifeste(null)));
        Assert.Empty(LectureAvecVerrous.Lignes(Manifeste("")));
        Assert.Equal([Couteau, "0x10EAF5,1,value,0x0A"], LectureAvecVerrous.Lignes(Manifeste(Couteau + ";0x10EAF5,1,value,0x0A")));
    }

    [Fact]
    public void Seul_le_fichier_d_une_lecture_s_efface_a_la_fin()
    {
        var certifie = Path.Combine(_dossier, "certified.txt");
        File.WriteAllText(certifie, "rom=mslug3\nopt.fbneo-cpu-speed-adjust=100%\n");
        LectureAvecVerrous.EffacerSiLecture(certifie);
        Assert.True(File.Exists(certifie));

        File.WriteAllText(certifie, "rom=mslug3\npipe=off\nlock.00=" + Couteau + "\n");
        LectureAvecVerrous.EffacerSiLecture(certifie);
        Assert.False(File.Exists(certifie));
    }

    [Fact]
    public void L_enregistreur_garde_les_verrous_de_l_attestation()
    {
        Assert.Equal(Couteau, ReplayRecorderService.VerrousDeLAttestation(JsonSerializer.SerializeToElement(new { Locks = Couteau })));
        Assert.Null(ReplayRecorderService.VerrousDeLAttestation(JsonSerializer.SerializeToElement(new { Locks = "" })));
        Assert.Null(ReplayRecorderService.VerrousDeLAttestation(JsonSerializer.SerializeToElement(new { WrapperVersion = "0.342.0.0" })));
    }

    [Fact]
    public void Un_manifeste_d_avant_les_verrous_se_relit_sans_verrou()
    {
        const string ancien = """{"RuntimeId":"nelfe-arcade-r1","RetroarchVersion":"1.22.2","ReplayFormat":"bsv","CoreName":"fbneo"}""";
        var runtime = JsonSerializer.Deserialize<ReplayRuntime>(ancien);
        Assert.Null(runtime!.Locks);
    }
}
