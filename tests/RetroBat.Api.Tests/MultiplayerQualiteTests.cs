using RetroBat.Api.Multiplayer;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le releve d'une partie en ligne, la porte de l'hote et la fermeture repetee (31/10).</summary>
public sealed class MultiplayerQualiteTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfeplay-qualite-" + Guid.NewGuid().ToString("N"));

    public MultiplayerQualiteTests() => Directory.CreateDirectory(_dossier);

    public void Dispose() => Directory.Delete(_dossier, recursive: true);

    [Fact]
    public void Le_ping_se_lit_dans_toutes_les_langues_et_l_hote_garde_le_pire()
    {
        string[] journal =
        [
            "[INFO] [Netplay] Vous avez rejoint le jeu en tant que joueur 2 (ping: 79 ms)",
            "[INFO] [Netplay] NELFEPLAY_player a rejoint le jeu en tant que joueur 3 (ping: 142 ms)",
            "[INFO] [Netplay] player has joined as player 4 (ping: 31 ms)",
        ];

        Assert.Equal(142, MultiplayerQualite.PingDe(journal));
        Assert.Null(MultiplayerQualite.PingDe(["[INFO] [Core] Unloading game..."]));
    }

    [Fact]
    public void Le_journal_de_l_API_ne_garde_que_les_dernieres_lignes_du_netplay()
    {
        var journal = Enumerable.Range(1, 30).Select(i => $"[INFO] [Netplay] ligne {i}\r").Append("[INFO] [Core] autre").ToList();

        var lignes = MultiplayerQualite.LignesDuNetplay(journal);

        Assert.Equal(MultiplayerQualite.LignesGardees, lignes.Count);
        Assert.Equal("[INFO] [Netplay] ligne 30", lignes[^1]);
        Assert.Empty(MultiplayerQualite.LignesDuNetplay(["[INFO] [Core] autre"]));
    }

    [Fact]
    public void Seules_les_copies_les_plus_recentes_du_journal_restent()
    {
        var journal = Path.Combine(_dossier, "es_launch_stdout.log");
        File.WriteAllText(journal, "partie");
        var copies = Path.Combine(_dossier, "copies");

        for (var i = 0; i < MultiplayerQualite.CopiesGardees + 3; i++)
        {
            MultiplayerQualite.Garder(journal, copies, $"202610{i + 10}-000000-abcd.log");
        }

        var restantes = Directory.GetFiles(copies).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(MultiplayerQualite.CopiesGardees, restantes.Count);
        Assert.Equal("20261013-000000-abcd.log", restantes[0]);
    }

    [Fact]
    public void Un_journal_absent_ne_fait_pas_tomber_le_releve()
    {
        var absent = Path.Combine(_dossier, "absent.log");

        Assert.Empty(MultiplayerQualite.Lire(absent));
        MultiplayerQualite.Garder(absent, Path.Combine(_dossier, "copies"), "x.log");
        Assert.False(Directory.Exists(Path.Combine(_dossier, "copies")));
    }

    [Theory]
    [InlineData(2, false, 1)]
    [InlineData(4, false, 3)]
    [InlineData(5, false, 4)]
    [InlineData(2, true, 2)]
    [InlineData(1, false, 1)]
    public void L_hote_n_ouvre_que_les_places_attendues(int joueurs, bool spectateur, int attendu)
        => Assert.Equal(attendu, MultiplayerLaunchPlan.ConnexionsDeLHote(joueurs, spectateur));

    [Fact]
    public async Task La_fermeture_insiste_tant_que_RetroArch_tourne()
    {
        var envois = 0;
        var fois = await RetroArchInvite.InsisterAsync(() => { envois++; return Task.CompletedTask; }, () => envois < 3, TimeSpan.Zero, 24);

        Assert.Equal(3, envois);
        Assert.Equal(3, fois);
    }

    [Fact]
    public async Task La_fermeture_part_une_fois_meme_si_RetroArch_semble_sorti_et_s_arrete_au_plafond()
    {
        var envois = 0;
        await RetroArchInvite.InsisterAsync(() => { envois++; return Task.CompletedTask; }, () => false, TimeSpan.Zero, 24);
        Assert.Equal(1, envois);

        await RetroArchInvite.InsisterAsync(() => { envois++; return Task.CompletedTask; }, () => true, TimeSpan.Zero, 5);
        Assert.Equal(6, envois);
    }
}
