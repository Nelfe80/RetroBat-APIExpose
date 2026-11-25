using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// (24/11) LE REPLAY TROP LONG. Le record de Donut Secret 1 (SMW, NeoGeoff, 20/11) : 4291 images pour un run de 2749, une
/// tentative abandonnee avant le run (chrono qui ne partait pas depuis l'etat, avant la 1.9.211). Le replay est garde
/// (decision du user) ; il court sur un coeur hote qui retrouve son run (dernier depart), jamais sur un ancien.
/// </summary>
public class FantomesTropLongsTests
{
    private const string Snes9xGhostsPublie = "32fe78e111fa8b2ea922365bce063bb14c4c087f56ded99488c0b26fb7e81fac";
    private const string Snes9xGhostsDuDernierDepart = "2a9c9e2a1987a7254971f176acddfbaaf904987a53e321fd6c461369dd87473c";

    private static FantomesDuSpeedrun.Hote Hote(string groupe) => FantomesDuSpeedrun.Liste.First(h => h.Groupe == groupe);

    [Theory]
    [InlineData(4291, 2749, true)]    // NeoGeoff, Donut Secret 1 : 1542 images avant le run
    [InlineData(3763, 2381, true)]    // Old AF Gamer, Yoshi's Island 3 : 1382
    [InlineData(3233, 3219, false)]   // Old AF Gamer, Donut Secret 1 du 24/11 : 14
    [InlineData(2833, 2749, false)]   // 84 : le plus long des replays propres mesures
    [InlineData(2989, 2749, false)]   // 240 : la limite
    [InlineData(2990, 2749, true)]
    [InlineData(1000, 0, false)]      // duree inconnue
    public void Un_replay_est_trop_long_au_dela_de_son_run_et_de_quelques_images(long images, long duree, bool attendu)
        => Assert.Equal(attendu, FantomesDuSpeedrun.ReplayTropLong(images, duree));

    [Fact]
    public void Le_coeur_publie_rejouerait_la_tentative_abandonnee_le_coeur_corrige_retrouve_le_run()
    {
        var smw = Hote("super-mario-world");
        Assert.False(FantomesDuSpeedrun.PeutCourir(smw, 4291, 2749, Snes9xGhostsPublie));
        Assert.False(FantomesDuSpeedrun.PeutCourir(smw, 4291, 2749, Snes9xGhostsPublie.ToUpperInvariant()));
        Assert.True(FantomesDuSpeedrun.PeutCourir(smw, 4291, 2749, Snes9xGhostsDuDernierDepart));
        // une empreinte inconnue est un ancien coeur
        Assert.False(FantomesDuSpeedrun.PeutCourir(smw, 4291, 2749, null));
        Assert.False(FantomesDuSpeedrun.PeutCourir(smw, 4291, 2749, ""));
    }

    [Fact]
    public void Un_replay_propre_court_sur_tous_les_coeurs()
    {
        var smw = Hote("super-mario-world");
        Assert.True(FantomesDuSpeedrun.PeutCourir(smw, 3233, 3219, Snes9xGhostsPublie));
        Assert.True(FantomesDuSpeedrun.PeutCourir(smw, 3233, 3219, null));
        // longueur ou duree inconnue : rien a juger, les autres gardes s'appliquent
        Assert.True(FantomesDuSpeedrun.PeutCourir(smw, null, 2749, Snes9xGhostsPublie));
        Assert.True(FantomesDuSpeedrun.PeutCourir(smw, 4291, null, Snes9xGhostsPublie));
    }

    [Fact]
    public void Le_jeu_complet_parti_du_demarrage_n_est_pas_concerne()
    {
        // Super Metroid : le replay commence au demarrage, sa longue ouverture est normale et le coeur s'aligne sur la partie
        Assert.True(FantomesDuSpeedrun.PeutCourir(Hote("super-metroid"), 90000, 60000, Snes9xGhostsPublie));
    }

    [Fact]
    public void Les_coeurs_publies_des_trois_familles_partent_du_premier_depart()
    {
        // fceumm_ghosts 0.13, genesis_plus_gx_ghosts 0.12, snes9x_ghosts 0.12 : ceux de outils.json au 24/11
        foreach (var e in new[] { "3a4d174a3d0cfcfac3315dfe1cf8f1a5a162075ae6a6320ee6ba7534c7426c43",
                     "8247886049b5af3e5c6c3c05db0ce557237e7777b8245da704c9c2a1bb6c2fdb", Snes9xGhostsPublie })
        {
            Assert.Contains(e, FantomesDuSpeedrun.CoeursQuiPartentDuPremierDepart);
        }

        Assert.Equal(29, FantomesDuSpeedrun.CoeursQuiPartentDuPremierDepart.Count);   // 9 genesis_plus_gx, 8 snes9x, 12 fceumm
        Assert.DoesNotContain(Snes9xGhostsDuDernierDepart, FantomesDuSpeedrun.CoeursQuiPartentDuPremierDepart);
    }

    [Fact]
    public void Le_coeur_snes_livre_en_1_9_215_est_reconnu_comme_un_coeur_qui_retrouve_son_run()
    {
        // snes9x_ghosts du 24/11 (livre par outils.json avec la 1.9.215)
        Assert.Contains(Snes9xGhostsDuDernierDepart, FantomesDuSpeedrun.CoeursQuiRetrouventLeRun);
        Assert.True(FantomesDuSpeedrun.CoeursQuiRetrouventLeRun.Contains(Snes9xGhostsDuDernierDepart.ToUpperInvariant()));
        Assert.Empty(FantomesDuSpeedrun.CoeursQuiRetrouventLeRun.Intersect(FantomesDuSpeedrun.CoeursQuiPartentDuPremierDepart, StringComparer.OrdinalIgnoreCase));
        Assert.True(FantomesDuSpeedrun.PeutCourir(Hote("super-mario-world"), 4291, 2749, Snes9xGhostsDuDernierDepart.ToUpperInvariant()));
    }

    private const string FceummGhostsDuDernierDepart = "b924f2686d71760ca93321bbe86711494604edd0f03bae50f7b39bd81fe79a59";
    private const string GenesisGhostsDuDernierDepart = "3f0a4affd375b505e74a58fb25575f66da9c9f1e2e83b6ac3c9d2cdad09bcd29";

    [Fact]
    public void Les_coeurs_nes_et_mega_drive_livres_en_1_9_217_retrouvent_leur_run()
    {
        // fceumm_ghosts 0.14 et genesis_plus_gx_ghosts 0.13 du 24/11, essayes au banc le 25/11 (SMB 1-1, Green Hill 1) et livres
        // par outils.json avec la 1.9.217 : les trois coeurs du dernier depart, et eux seuls
        Assert.Equal(3, FantomesDuSpeedrun.CoeursQuiRetrouventLeRun.Count);
        Assert.Contains(FceummGhostsDuDernierDepart, FantomesDuSpeedrun.CoeursQuiRetrouventLeRun);
        Assert.Contains(GenesisGhostsDuDernierDepart, FantomesDuSpeedrun.CoeursQuiRetrouventLeRun);
        Assert.Empty(FantomesDuSpeedrun.CoeursQuiRetrouventLeRun.Intersect(FantomesDuSpeedrun.CoeursQuiPartentDuPremierDepart, StringComparer.OrdinalIgnoreCase));
        // banc du 25/11 : SMB 1291 images pour un run de 600, Sonic 1340 pour 600 (une tentative qui meurt, puis le run)
        var smb = Hote("super-mario-bros");
        var sonic = Hote("sonic-the-hedgehog");
        Assert.True(FantomesDuSpeedrun.PeutCourir(smb, 1291, 600, FceummGhostsDuDernierDepart));
        Assert.True(FantomesDuSpeedrun.PeutCourir(smb, 1291, 600, FceummGhostsDuDernierDepart.ToUpperInvariant()));
        Assert.True(FantomesDuSpeedrun.PeutCourir(sonic, 1340, 600, GenesisGhostsDuDernierDepart));
        // les coeurs publies avant (fceumm 0.13, genesis 0.12) rejoueraient la tentative abandonnee
        Assert.False(FantomesDuSpeedrun.PeutCourir(smb, 1291, 600, "3a4d174a3d0cfcfac3315dfe1cf8f1a5a162075ae6a6320ee6ba7534c7426c43"));
        Assert.False(FantomesDuSpeedrun.PeutCourir(sonic, 1340, 600, "8247886049b5af3e5c6c3c05db0ce557237e7777b8245da704c9c2a1bb6c2fdb"));
        // un replay propre court sur les deux
        Assert.True(FantomesDuSpeedrun.PeutCourir(smb, 724, 600, FceummGhostsDuDernierDepart));
        Assert.True(FantomesDuSpeedrun.PeutCourir(sonic, 798, 600, "8247886049b5af3e5c6c3c05db0ce557237e7777b8245da704c9c2a1bb6c2fdb"));
    }
}
