using RetroBat.Api.Infrastructure;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Netplay;
using RetroBat.Api.Replay.Recording;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// REPLAY EN NETPLAY (lot 3 de RetroArch NelfePlay, nelfeplay.2, 25/11) : l'API reconnait l'exe du lot 3, enregistre le replay de
/// l'invite JOUEUR seulement quand il est en place, et coupe le controle des images (netplay_check_frames) aux lancements
/// MULTIPLAYER sous cet exe. L'exe livre par outils.json reste nelfeplay.1.
/// </summary>
public sealed class ReplayNetplayTests
{
    [Fact]
    public void Seul_le_joueur_invite_enregistre_et_seulement_sous_l_exe_du_lot_3()
    {
        Assert.Equal(ReplayNetplay.Porte.Habituelle, ReplayNetplay.Decider(NetplayGuestService.Role.Aucun, true));
        Assert.Equal(ReplayNetplay.Porte.Habituelle, ReplayNetplay.Decider(NetplayGuestService.Role.Aucun, false));
        Assert.Equal(ReplayNetplay.Porte.InviteEnregistre, ReplayNetplay.Decider(NetplayGuestService.Role.Joueur, true));
        Assert.Equal(ReplayNetplay.Porte.InviteSansExe, ReplayNetplay.Decider(NetplayGuestService.Role.Joueur, false));
        Assert.Equal(ReplayNetplay.Porte.Spectateur, ReplayNetplay.Decider(NetplayGuestService.Role.Spectateur, true));
        Assert.Equal(ReplayNetplay.Porte.Spectateur, ReplayNetplay.Decider(NetplayGuestService.Role.Spectateur, false));
    }

    [Fact]
    public void Les_deux_exes_NelfePlay_sont_connus_mais_seul_le_lot_3_enregistre_le_netplay()
    {
        Assert.Contains(RetroArchNelfePlay.ExeNelfePlay, RetroArchNelfePlay.ExesConnus);
        Assert.Contains(RetroArchNelfePlay.ExeNelfePlay2, RetroArchNelfePlay.ExesConnus);
        Assert.Equal("a1aff87f0c6f64d675709253067e6c6e3d79450c6ae777aa85e18beda48dbda2", RetroArchNelfePlay.ExeNelfePlay2);
        Assert.DoesNotContain(RetroArchNelfePlay.ExeNelfePlay, RetroArchNelfePlay.ExesQuiEnregistrentLeNetplay);
        Assert.Contains(RetroArchNelfePlay.ExeNelfePlay2, RetroArchNelfePlay.ExesQuiEnregistrentLeNetplay);
        // L'exe livre reste le lot 1 : outils.json n'est pas touche avant l'essai entre deux bornes.
        Assert.EndsWith("nelfeplay.1.exe", RetroArchNelfePlay.CheminDeLExe);
    }

    [Fact]
    public void Marque_et_empreinte_du_lot_3_levent_les_portes_et_le_netplay_le_lot_1_seulement_les_portes()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "ra-netplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dossier, ".nelfeplay"));
        try
        {
            var exe = Path.Combine(dossier, "retroarch.exe");
            File.WriteAllText(exe, "exe du lot 3");
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(dossier, ".nelfeplay", "build.txt"), "build=1.22.2-nelfeplay.2\r\nexe=" + sha + "\r\n");

            // Empreinte inconnue de l'API : rien.
            Assert.False(ReplayRecorderService.RetroArchNelfePlayInstalle(dossier));
            Assert.False(ReplayRecorderService.RetroArchNelfePlayNetplay(dossier));
            // Connue comme exe NelfePlay SANS savoir le netplay (lot 1) : portes levees, replay de l'invite non.
            Assert.True(ReplayRecorderService.RetroArchNelfePlayInstalle(dossier, new[] { sha }));
            Assert.False(ReplayRecorderService.RetroArchNelfePlayNetplay(dossier, new[] { new string('0', 64) }));
            // Exe du lot 3 : les deux.
            Assert.True(ReplayRecorderService.RetroArchNelfePlayNetplay(dossier, new[] { sha }));
            // RetroBat remet son exe sous la marque : le netplay se referme aussi.
            File.WriteAllText(exe, "la 1.22.2 de RetroBat");
            File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(1));
            Assert.False(ReplayRecorderService.RetroArchNelfePlayNetplay(dossier, new[] { sha }));
        }
        finally
        {
            Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public void Le_lot_3_en_place_sous_sa_marque_n_est_pas_pris_pour_une_mise_a_jour_de_RetroBat()
    {
        var etat = new EtatNelfePlay(true, false, false, true, RetroArchNelfePlay.ExeNelfePlay2, RetroArchNelfePlay.ExeNelfePlay2,
            true, true, true, false);
        Assert.Equal(ActionNelfePlay.Rien, RetroArchNelfePlayService.Decider(etat));
        // Marque du lot 1 mais exe du lot 3 : l'API y voit un exe etranger a sa marque (retrait propre, rien restaure). C'est
        // pourquoi l'essai pose le lot 3 AVEC SA marque (essai_borne.py poser2), jamais l'exe seul par-dessus.
        Assert.Equal(ActionNelfePlay.Retirer, RetroArchNelfePlayService.Decider(etat with { ExeDeLaMarque = RetroArchNelfePlay.ExeNelfePlay }));
    }

    [Fact]
    public void Le_controle_des_images_se_coupe_aux_lancements_multiplayer_sous_l_exe_du_lot_3_seulement()
    {
        var sous = MultiplayerLaunchPlan.ReglagesDuReplayEnNetplay(true);
        Assert.Single(sous);
        Assert.Equal("0", sous["netplay_check_frames"]);
        Assert.Empty(MultiplayerLaunchPlan.ReglagesDuReplayEnNetplay(false));
    }
}
