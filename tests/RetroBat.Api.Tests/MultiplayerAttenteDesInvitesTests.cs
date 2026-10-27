using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE DEPART ATTEND TOUT LE MONDE (27/10, TGM2) : l'hote passe son depart pret au ralenti, lit l'arrivee des invites dans le
/// journal de RetroArch, repart a vitesse normale, puis leve tout ralenti qui dure. La vitesse se lit au temps de reponse
/// de GET_STATUS (sonde_ralenti.py : ~16 ms, ~500 ms au ralenti x30). Les lignes de journal sont celles des bancs du 27/10.
/// </summary>
public class MultiplayerAttenteDesInvitesTests
{
    public MultiplayerAttenteDesInvitesTests() => MultiplayerAttenteDesInvites.DelaiDeBascule = TimeSpan.FromMilliseconds(50);

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
    public void L_hote_recoit_le_ralenti_et_lit_le_journal_que_lui_donne_le_lanceur()
    {
        Assert.True(MultiplayerAttenteDesInvites.EnService);
        // Le lanceur passe toujours --log-file es_launch_stdout.log : un log_dir pose dans retroarch.cfg n'y changerait rien.
        Assert.Equal(new Dictionary<string, string> { ["slowmotion_ratio"] = "30.000000" }, MultiplayerAttenteDesInvites.Reglages);
        Assert.Equal(RetroBat.Domain.Paths.RetroBatPaths.EsLaunchStdoutLogPath, MultiplayerAttenteDesInvites.CheminDuJournal);
    }

    [Fact]
    public async Task Le_ralenti_se_verifie_et_la_vitesse_revient()
    {
        var ra = new RetroArchSimule("PLAYING");
        var attente = new MultiplayerAttenteDesInvites(ra);
        Assert.True(await attente.MettreAuRalentiAsync(CancellationToken.None));
        Assert.True(ra.AuRalenti);
        Assert.Equal(["SLOWMOTION"], ra.Envoyees);
        Assert.True(await attente.RetrouverLaVitesseAsync(CancellationToken.None));
        Assert.False(ra.AuRalenti);
        Assert.Equal(["SLOWMOTION", "SLOWMOTION"], ra.Envoyees);
        // Deja a vitesse normale : rien a basculer.
        Assert.True(await attente.RetrouverLaVitesseAsync(CancellationToken.None));
        Assert.Equal(2, ra.Envoyees.Count);
    }

    [Fact]
    public async Task Une_bascule_perdue_se_rattrape()
    {
        var ra = new RetroArchSimule("PLAYING", basculesPerdues: 1);
        Assert.True(await new MultiplayerAttenteDesInvites(ra).MettreAuRalentiAsync(CancellationToken.None));
        Assert.True(ra.AuRalenti);
        Assert.Equal(["SLOWMOTION", "SLOWMOTION"], ra.Envoyees);
    }

    [Fact]
    public async Task Sans_ralenti_ou_sans_jeu_la_partie_part_comme_avant()
    {
        // Sans son, RetroArch ne ralentit pas : la partie part comme avant.
        var sourd = new RetroArchSimule("PLAYING", ralentiImpossible: true);
        Assert.False(await new MultiplayerAttenteDesInvites(sourd).MettreAuRalentiAsync(CancellationToken.None));
        Assert.False(sourd.AuRalenti);
        // Un jeu qui ne tourne pas encore n'est pas ralenti a l'aveugle.
        var absent = new RetroArchSimule(null);
        Assert.False(await new MultiplayerAttenteDesInvites(absent).MettreAuRalentiAsync(CancellationToken.None));
        Assert.Empty(absent.Envoyees);
    }

    [Fact]
    public async Task Les_invites_s_attendent_dans_le_journal_au_plus_la_patience()
    {
        var journal = Path.Combine(Path.GetTempPath(), "nelfe-attente-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(journal, "[INFO] [Netplay] You have joined as player 1\n[INFO] [Netplay] b has joined as player 2 (ping: 20 ms)\n");
            var ra = new RetroArchSimule("PLAYING");
            var attente = new MultiplayerAttenteDesInvites(ra);
            Assert.Equal(1, await attente.AttendreLesInvitesAsync(journal, 1, TimeSpan.FromSeconds(5), CancellationToken.None));
            var debut = DateTime.UtcNow;
            Assert.Equal(1, await attente.AttendreLesInvitesAsync(journal, 3, TimeSpan.FromMilliseconds(600), CancellationToken.None));
            Assert.True(DateTime.UtcNow - debut < TimeSpan.FromSeconds(3));
            // Au ralenti, RetroArch traite le reseau tout seul : l'attente ne lui envoie rien.
            Assert.Empty(ra.Envoyees);
            // Sans journal (RetroArch ne l'a pas encore cree), personne.
            Assert.Equal(0, await attente.AttendreLesInvitesAsync(journal + ".absent", 1, TimeSpan.Zero, CancellationToken.None));
        }
        finally
        {
            File.Delete(journal);
        }
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
    public async Task Un_ralenti_qui_dure_pendant_la_partie_est_leve()
    {
        var ra = new RetroArchSimule("PLAYING") { AuRalenti = true };
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var levees = await new MultiplayerAttenteDesInvites(ra).GarderLaVitesseAsync(TimeSpan.FromMilliseconds(100), fin.Token);
        Assert.True(levees >= 1);
        Assert.False(ra.AuRalenti);
    }

    /// <summary>
    /// SUR UN VRAI RETROARCH (banc d'essai, jamais une partie de la borne ; son actif, sinon le ralenti n'agit pas) : TGM2
    /// heberge, son depart pret charge ; le test le passe au ralenti, le banc connecte l'invite quelques secondes plus tard,
    /// le test le voit dans le journal et repart a vitesse normale. Passe sans rien faire sans le banc
    /// (NELFEPLAY_BANC_RA_PORT, NELFEPLAY_BANC_JOURNAL).
    /// </summary>
    [Fact]
    public async Task Sur_un_vrai_RetroArch_l_hote_au_ralenti_attend_l_invite_puis_repart()
    {
        var port = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_RA_PORT");
        var journal = Environment.GetEnvironmentVariable("NELFEPLAY_BANC_JOURNAL");
        if (port is null || journal is null)
        {
            return;   // banc non monte
        }

        var attente = new MultiplayerAttenteDesInvites(new CommandesRetroArchUdp(int.Parse(port, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(await attente.MettreAuRalentiAsync(CancellationToken.None));
        Assert.Equal(1, await attente.AttendreLesInvitesAsync(journal, 1, TimeSpan.FromSeconds(60), CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(await attente.RetrouverLaVitesseAsync(CancellationToken.None));
    }

    /// <summary>
    /// RetroArch tel que ses commandes le montrent : GET_STATUS, qui repond lentement au ralenti (RetroArch ne traite ses
    /// commandes qu'entre deux images), et SLOWMOTION qui bascule (ou non) le ralenti.
    /// </summary>
    private sealed class RetroArchSimule(string? etat, int basculesPerdues = 0, bool ralentiImpossible = false) : ICommandesRetroArch
    {
        private int _perdues = basculesPerdues;

        public bool AuRalenti { get; set; }
        public List<string> Envoyees { get; } = [];

        public async Task<string?> DemanderAsync(string commande, CancellationToken ct, int delaiMs = 500)
        {
            if (commande != "GET_STATUS" || etat is null)
            {
                return null;
            }

            if (AuRalenti)
            {
                await Task.Delay(300, ct);
            }

            return "GET_STATUS " + etat + " fb_alpha,tgm2,crc32=ff408c75";
        }

        public Task EnvoyerAsync(string commande, CancellationToken ct)
        {
            Envoyees.Add(commande);
            if (commande == "SLOWMOTION" && !ralentiImpossible)
            {
                if (_perdues > 0)
                {
                    _perdues--;
                }
                else
                {
                    AuRalenti = !AuRalenti;
                }
            }

            return Task.CompletedTask;
        }
    }
}
