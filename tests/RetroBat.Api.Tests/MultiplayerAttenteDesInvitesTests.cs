using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEPART ATTEND TOUT LE MONDE (27/10, TGM2) : l'hote met son depart pret en pause, lit l'arrivee des invites dans le
/// journal de RetroArch, reprend, puis leve toute pause qui dure. Les lignes de journal sont celles du banc du 27/10.
/// </summary>
public class MultiplayerAttenteDesInvitesTests
{
    [Fact]
    public void Le_journal_dit_quels_invites_sont_a_leur_place()
    {
        string[] journal =
        [
            "[INFO] [Netplay] You have joined as player 1",
            "[INFO] [Netplay] Connection slot 0",
            "[INFO] [Netplay] Got connection from: \"client_pause\"",
            "[INFO] [Netplay] client_pause has joined as player 2 (ping: 130 ms)",
            "[INFO] [Netplay] regard has joined as spectator",
        ];
        Assert.Equal(new Dictionary<string, int> { ["client_pause"] = 2 }, MultiplayerAttenteDesInvites.InvitesALeurPlace(journal));
        // Deux ports par borne (Title Fight, banc du 27/10) : RetroArch dit « with input devices ».
        Assert.Equal(new Dictionary<string, int> { ["client_ports"] = 2 }, MultiplayerAttenteDesInvites.InvitesALeurPlace(
            ["[INFO] [Netplay] You have joined with input devices 1, 3", "[INFO] [Netplay] client_ports has joined with input devices 2, 4 (ping: 46 ms)"]));
        // Un invite parti ne compte plus.
        Assert.Empty(MultiplayerAttenteDesInvites.InvitesALeurPlace([.. journal, "[INFO] [Netplay] \"client_pause\" has disconnected"]));
    }

    [Fact]
    public void L_etat_de_retroarch_se_lit_dans_get_status()
    {
        Assert.Equal("PAUSED", MultiplayerAttenteDesInvites.Etat("GET_STATUS PAUSED fb_alpha,tgm2,crc32=ff408c75"));
        Assert.Equal("PLAYING", MultiplayerAttenteDesInvites.Etat("GET_STATUS PLAYING fb_alpha,tgm2,crc32=ff408c75\n"));
        Assert.Equal("CONTENTLESS", MultiplayerAttenteDesInvites.Etat("GET_STATUS CONTENTLESS"));
        Assert.Null(MultiplayerAttenteDesInvites.Etat(null));
        Assert.Null(MultiplayerAttenteDesInvites.Etat("READ_CORE_RAM 0 -1"));
    }

    [Fact]
    public void L_hote_recoit_la_pause_et_lit_le_journal_que_lui_donne_le_lanceur()
    {
        // Le lanceur passe toujours --log-file es_launch_stdout.log : un log_dir pose dans retroarch.cfg n'y changerait rien.
        Assert.Equal(new Dictionary<string, string> { ["netplay_allow_pausing"] = "true" }, MultiplayerAttenteDesInvites.Reglages);
        Assert.Equal(RetroBat.Domain.Paths.RetroBatPaths.EsLaunchStdoutLogPath, MultiplayerAttenteDesInvites.CheminDuJournal);
    }

    [Fact]
    public async Task La_pause_se_verifie_et_un_refus_laisse_partir_le_jeu()
    {
        var accepte = new RetroArchSimule("PLAYING");
        Assert.True(await new MultiplayerAttenteDesInvites(accepte).MettreEnPauseAsync(CancellationToken.None));
        Assert.Equal(["PAUSE_TOGGLE"], accepte.Envoyees);
        Assert.True(await new MultiplayerAttenteDesInvites(accepte).ReprendreAsync(CancellationToken.None));
        Assert.Equal("PLAYING", accepte.Etat);

        // RetroArch sans netplay_allow_pausing ignore la commande : la partie part comme avant.
        var refuse = new RetroArchSimule("PLAYING", pauseRefusee: true);
        Assert.False(await new MultiplayerAttenteDesInvites(refuse).MettreEnPauseAsync(CancellationToken.None));
        // Un jeu qui ne tourne pas encore n'est pas mis en pause a l'aveugle.
        var absent = new RetroArchSimule(null);
        Assert.False(await new MultiplayerAttenteDesInvites(absent).MettreEnPauseAsync(CancellationToken.None));
        Assert.Empty(absent.Envoyees);
    }

    [Fact]
    public async Task Les_invites_s_attendent_dans_le_journal_au_plus_la_patience()
    {
        var journal = Path.Combine(Path.GetTempPath(), "nelfe-attente-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(journal, "[INFO] [Netplay] You have joined as player 1\n[INFO] [Netplay] b has joined as player 2 (ping: 20 ms)\n");
            var ra = new RetroArchSimule("PAUSED");
            var attente = new MultiplayerAttenteDesInvites(ra);
            Assert.Equal(1, await attente.AttendreLesInvitesAsync(journal, 1, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Empty(ra.Envoyees);
            var debut = DateTime.UtcNow;
            Assert.Equal(1, await attente.AttendreLesInvitesAsync(journal, 3, TimeSpan.FromMilliseconds(600), CancellationToken.None));
            Assert.True(DateTime.UtcNow - debut < TimeSpan.FromSeconds(3));
            // Un RetroArch en pause ne traite pas le reseau : pendant l'attente, il avance d'une image a la fois.
            Assert.NotEmpty(ra.Envoyees);
            Assert.All(ra.Envoyees, c => Assert.Equal("FRAMEADVANCE", c));
            // Sans journal (RetroArch ne l'a pas encore cree), personne.
            Assert.Equal(0, await attente.AttendreLesInvitesAsync(journal + ".absent", 1, TimeSpan.Zero, CancellationToken.None));
        }
        finally
        {
            File.Delete(journal);
        }
    }

    [Fact]
    public void L_attente_en_pause_est_hors_service()
    {
        // 27/10 au soir (banc_pause_relais.py) : un invite connecte pendant la pause restait fige apres la reprise.
        Assert.False(MultiplayerAttenteDesInvites.EnService);
    }

    [Fact]
    public async Task La_partie_se_ferme_quand_le_dernier_invite_est_parti()
    {
        var journal = Path.Combine(Path.GetTempPath(), "nelfe-depart-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(journal, "[INFO] [Netplay] You have joined as player 2\n");
            var attente = new MultiplayerAttenteDesInvites(new RetroArchSimule("PLAYING"));
            var suivi = attente.AttendreLeDepartDesInvitesAsync(journal, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            // Personne n'est encore venu : la partie continue.
            await Task.Delay(300);
            Assert.False(suivi.IsCompleted);
            File.AppendAllText(journal, "[INFO] [Netplay] a has joined as player 1 (ping: 20 ms)\n[INFO] [Netplay] b has joined as player 3 (ping: 30 ms)\n");
            await Task.Delay(300);
            // Un des deux s'en va : l'autre joue encore.
            File.AppendAllText(journal, "[INFO] [Netplay] \"a\" has disconnected\n");
            await Task.Delay(300);
            Assert.False(suivi.IsCompleted);
            File.AppendAllText(journal, "[INFO] [Netplay] \"b\" has disconnected\n");
            Assert.Same(suivi, await Task.WhenAny(suivi, Task.Delay(TimeSpan.FromSeconds(5))));

            // La fin de la partie arrete le suivi.
            using var fin = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            File.WriteAllText(journal, "[INFO] [Netplay] a has joined as player 1 (ping: 20 ms)\n");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => attente.AttendreLeDepartDesInvitesAsync(journal, TimeSpan.FromMilliseconds(50), fin.Token));
        }
        finally
        {
            File.Delete(journal);
        }
    }

    [Fact]
    public async Task Une_pause_qui_dure_pendant_la_partie_est_levee()
    {
        var ra = new RetroArchSimule("PAUSED");
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(3.4));
        var levees = await new MultiplayerAttenteDesInvites(ra).GarderSansPauseAsync(fin.Token);
        Assert.True(levees >= 1);
        Assert.Equal("PLAYING", ra.Etat);
    }

    /// <summary>
    /// SUR UN VRAI RETROARCH (banc d'essai, jamais une partie de la borne ; banc_attente.py) : TGM2 heberge, son depart pret
    /// charge ; le test le met en pause, le banc connecte l'invite quelques secondes plus tard, le test le voit dans le
    /// journal et reprend. Passe sans rien faire sans le banc (NELFEPLAY_BANC_RA_PORT, NELFEPLAY_BANC_JOURNAL).
    /// </summary>
    [Fact]
    public async Task Sur_un_vrai_RetroArch_l_hote_en_pause_attend_l_invite_puis_reprend()
    {
        var port = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RA_PORT");
        var journal = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_JOURNAL");
        if (port is null || journal is null)
        {
            return;   // banc non monte
        }

        var attente = new MultiplayerAttenteDesInvites(new CommandesRetroArchUdp(int.Parse(port, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(await attente.MettreEnPauseAsync(CancellationToken.None));
        Assert.Equal(1, await attente.AttendreLesInvitesAsync(journal, 1, TimeSpan.FromSeconds(60), CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(await attente.ReprendreAsync(CancellationToken.None));
    }

    /// <summary>RetroArch tel que ses commandes le montrent : GET_STATUS, et PAUSE_TOGGLE qui bascule (ou non) son etat.</summary>
    private sealed class RetroArchSimule(string? etat, bool pauseRefusee = false) : ICommandesRetroArch
    {
        public string? Etat { get; private set; } = etat;
        public List<string> Envoyees { get; } = [];

        public Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
            => Task.FromResult(commande == "GET_STATUS" && Etat is not null ? "GET_STATUS " + Etat + " fb_alpha,tgm2,crc32=ff408c75" : null);

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            Envoyees.Add(commande);
            if (commande == "PAUSE_TOGGLE" && !pauseRefusee)
            {
                Etat = Etat == "PAUSED" ? "PLAYING" : Etat == "PLAYING" ? "PAUSED" : Etat;
            }

            return Task.CompletedTask;
        }
    }
}
