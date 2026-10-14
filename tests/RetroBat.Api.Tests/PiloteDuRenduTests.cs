using RetroBat.Api.Replay.Video;
using Xunit;
using A = RetroBat.Api.Replay.Video.PiloteDuRendu.Action;

namespace RetroBat.Api.Tests;

/// <summary>Le pilote du rendu video d'un replay (CDC video V2, §4.2).</summary>
public sealed class PiloteDuRenduTests
{
    private static PiloteDuRendu.Releve R(double s, int drapeaux, long frame) => new(s, drapeaux, frame);

    [Fact]
    public void Une_lecture_normale_filme_du_debut_a_la_fin_puis_quitte()
    {
        var p = new PiloteDuRendu(0, 1000);
        Assert.Equal(A.Rien, p.Decider(R(0.5, 0, 0)));
        Assert.Equal(A.DebutEnregistrement, p.Decider(R(1.0, 4, 5)));
        Assert.Equal(A.AvanceRapide, p.Decider(R(1.2, 4, 12)));
        Assert.Equal(A.Rien, p.Decider(R(2.0, 4, 500)));
        Assert.Equal(A.FinEnregistrement, p.Decider(R(3.0, 4, 1003)));
        Assert.Equal(A.Quitter, p.Decider(R(3.2, 4, 1010)));
        Assert.True(p.Complet);
        Assert.Null(p.Raison);
        Assert.Equal(A.Rien, p.Decider(R(3.4, 4, 1020)));
    }

    [Fact]
    public void L_enregistrement_attend_le_debut_de_la_performance()
    {
        var p = new PiloteDuRendu(800, 2000);
        Assert.Equal(A.AvanceRapide, p.Decider(R(1.0, 4, 5)));
        Assert.Equal(A.Rien, p.Decider(R(1.5, 4, 400)));
        Assert.Equal(A.Rien, p.Decider(R(1.7, 4, 799)));
        Assert.Equal(A.DebutEnregistrement, p.Decider(R(1.9, 4, 805)));
    }

    [Fact]
    public void Une_lecture_qui_ne_demarre_pas_est_abandonnee()
    {
        var p = new PiloteDuRendu(0, 1000, attenteDemarrage: 60);
        Assert.Equal(A.Rien, p.Decider(R(59, -1, 0)));
        Assert.Equal(A.Quitter, p.Decider(R(61, -1, 0)));
        Assert.Equal("demarrage", p.Raison);
        Assert.False(p.Complet);
    }

    [Fact]
    public void Un_replay_qui_s_arrete_avant_la_fin_donne_un_film_incomplet()
    {
        var p = new PiloteDuRendu(0, 27000, immobiliteMax: 5);
        p.Decider(R(1.0, 4, 5));
        p.Decider(R(1.2, 4, 10));
        Assert.Equal(A.Rien, p.Decider(R(20, 4, 9500)));
        Assert.Equal(A.Rien, p.Decider(R(24, -1, 9500)));
        Assert.Equal(A.FinEnregistrement, p.Decider(R(26, -1, 9500)));
        Assert.Equal(A.Quitter, p.Decider(R(26.2, -1, 9500)));
        Assert.Equal("fin_prematuree", p.Raison);
        Assert.False(p.Complet);
    }

    [Fact]
    public void Une_frame_qui_recule_ne_compte_pas_comme_une_progression()
    {
        var p = new PiloteDuRendu(0, 5000, immobiliteMax: 5);
        p.Decider(R(1.0, 4, 100));
        p.Decider(R(1.2, 4, 120));
        Assert.Equal(A.Rien, p.Decider(R(4, 4, 50)));
        Assert.Equal(120, p.FrameAtteinte);
        Assert.Equal(A.FinEnregistrement, p.Decider(R(7, 4, 60)));
    }

    [Fact]
    public void Le_plafond_arrete_proprement_un_enregistrement_en_cours()
    {
        var p = new PiloteDuRendu(0, 100000);
        p.Decider(R(1.0, 4, 5));
        p.Decider(R(1.2, 4, 10));
        Assert.Equal(A.FinEnregistrement, p.Abandonner());
        Assert.Equal(A.Quitter, p.Decider(R(2, 4, 20)));
        Assert.Equal("plafond", p.Raison);
        Assert.False(p.Complet);
    }
}

/// <summary>Le profil de l'instance de rendu reprend les reglages mesures au lot 0 (CDC video V2, §4.2).</summary>
public sealed class ProfilDeRenduTests
{
    [Fact]
    public void Le_profil_filme_l_image_du_coeur_agrandie_sans_toucher_aux_parties()
    {
        var profil = ReplayVideoRenderer.ProfilDeRendu(@"C:\rendu\sauvegardes", @"C:\rendu\films");
        Assert.Contains("network_cmd_port = \"55400\"", profil);
        Assert.Contains("video_gpu_record = \"false\"", profil);
        Assert.Contains("video_record_scale_factor = \"4\"", profil);
        Assert.Contains(@"recording_output_directory = ""C:\rendu\films""", profil);
        Assert.Contains("audio_mute_enable = \"true\"", profil);
        Assert.Contains("config_save_on_exit = \"false\"", profil);
        Assert.Contains(@"savefile_directory = ""C:\rendu\sauvegardes""", profil);
        Assert.DoesNotContain("video_driver", profil);
    }
}
