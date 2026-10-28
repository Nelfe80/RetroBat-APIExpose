using System.Text.Json;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le spectateur cache de l'hote (consoles reliees, 28/10) : ce qu'il montre, comment il se lance, ce qu'il change.</summary>
public class MultiplayerSpectateurTests
{
    private static readonly MultiplayerCore CoeurGba = JsonSerializer.Deserialize<MultiplayerCore>("""
        {
          "core": "mgba_splitscreen",
          "options": { "splitscreen_boot_menu": "off", "splitscreen_players": "{players}", "splitscreen_layout": "focus" },
          "seat_options": { "splitscreen_focus_player": "{seat}", "splitscreen_audio": "player {seat}" },
          "spectator": { "options": { "splitscreen_layout": "auto", "splitscreen_audio": "player 1" }, "grid": "auto" }
        }
        """)!;

    [Fact]
    public void Le_spectateur_montre_toutes_les_consoles_avec_les_options_de_la_partie()
    {
        Assert.NotNull(CoeurGba.Spectator);
        var options = MultiplayerSpectateur.OptionsDuSpectateur(CoeurGba, CoeurGba.Spectator!, 4);
        Assert.Equal("4", options["splitscreen_players"]);
        Assert.Equal("auto", options["splitscreen_layout"]);
        Assert.Equal("player 1", options["splitscreen_audio"]);
        Assert.Equal("off", options["splitscreen_boot_menu"]);

        Assert.Equal((2, 1), MultiplayerSpectateur.Grille("auto", 2));
        Assert.Equal((2, 2), MultiplayerSpectateur.Grille("auto", 3));
        Assert.Equal((2, 2), MultiplayerSpectateur.Grille("auto", 4));
        Assert.Equal((2, 1), MultiplayerSpectateur.Grille("2x1", 2));
        Assert.Equal((3, 1), MultiplayerSpectateur.Grille(null, 3));
    }

    [Fact]
    public void Le_spectateur_rejoint_la_session_du_relais_cache_et_muet()
    {
        var session = new MultiplayerSessionNetplay { Relay = "europe-west1.relay.retroarch.com", Port = 55435, Session = "ABC", SpectatePassword = "ps" };
        var arguments = MultiplayerSpectateur.Arguments("c.dll", "jeu.zip", "retroarch.cfg", "spect.cfg", "spect.log", "film.mkv", session);
        // Il filme des son lancement : aucune commande a lui envoyer, donc aucun port.
        Assert.Equal(["-L", "c.dll", "jeu.zip", "--config", "retroarch.cfg", "--appendconfig", "spect.cfg", "--verbose", "--log-file",
            "spect.log", "--record=film.mkv", "--connect=europe-west1.relay.retroarch.com", "--port=55435", "--nick=NelfePlayTV",
            "--mitm-session=ABC"], arguments);
        Assert.DoesNotContain("--mitm-session=", MultiplayerSpectateur.Arguments("c", "r", "a", "b", "l", "f", session with { Session = "" }).Last());

        var reglages = MultiplayerSpectateur.Reglages("opts.cfg", "sauv", "films", "ps");
        Assert.Contains("netplay_start_as_spectator = \"true\"", reglages);
        Assert.Contains("netplay_password = \"ps\"", reglages);
        // Son journal en anglais, lu par l'API sur toutes les bornes (« Connecte a » en francais, 28/10).
        Assert.Contains("user_language = \"0\"", reglages);
        // Aucune ecoute reseau, donc aucune demande du pare-feu (28/10).
        Assert.Contains("network_cmd_enable = \"false\"", reglages);
        Assert.Contains("network_remote_enable = \"false\"", reglages);
        Assert.DoesNotContain(reglages, l => l.StartsWith("network_cmd_port", StringComparison.Ordinal));
        Assert.Contains("input_overlay_enable = \"false\"", reglages);
        Assert.Contains("video_shader_enable = \"false\"", reglages);
        Assert.Contains("audio_mute_enable = \"true\"", reglages);
        Assert.Contains("recording_output_directory = \"films\"", reglages);
        Assert.Contains("input_exit_emulator_btn = \"nul\"", reglages);
    }

    /// <summary>
    /// LE BANC (python m6_spike/banc_spectateur_api.py) : un hote relaye, sa session dans NELFEPLAY_BANC_SPECTATEUR ({relay,
    /// port, session, password, rom}). Le spectateur de l'API le rejoint en filmant, puis sa fenetre se ferme : RetroArch
    /// termine le film en quittant.
    /// </summary>
    [Fact]
    public async Task Le_spectateur_de_l_api_rejoint_un_hote_relaye_et_se_filme()
    {
        if (Environment.GetEnvironmentVariable("NELFEPLAY_BANC_SPECTATEUR") is not { Length: > 0 } texte) return;
        using var doc = JsonDocument.Parse(texte);
        var r = doc.RootElement;
        var session = new MultiplayerSessionNetplay
        {
            Relay = r.GetProperty("relay").GetString()!, Port = r.GetProperty("port").GetInt32(),
            Session = r.GetProperty("session").GetString()!, SpectatePassword = r.GetProperty("password").GetString()!,
        };
        var options = MultiplayerSpectateur.OptionsDuSpectateur(CoeurGba, CoeurGba.Spectator!, 2);
        var journal = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        try
        {
            Assert.True(await MultiplayerSpectateur.LancerAsync(session, "mgba_splitscreen", r.GetProperty("rom").GetString()!, options,
                journal, CancellationToken.None), "le spectateur n'a pas rejoint la partie");
            Assert.True(MultiplayerSpectateur.Connecte);
            var film = MultiplayerSpectateur.Film;
            Assert.NotNull(film);
            Assert.True(File.Exists(film), "pas de film : " + film);
            await Task.Delay(10000);
            Assert.True(await MultiplayerSpectateur.FermerAsync(CancellationToken.None), "le spectateur n'a pas quitte");
            Assert.False(MultiplayerSpectateur.Connecte);
            Assert.True(new FileInfo(film!).Length > 100_000, "film trop petit : " + new FileInfo(film!).Length);
            File.Copy(film!, Path.Combine(r.GetProperty("sortie").GetString()!, "spectateur.mkv"), overwrite: true);
        }
        finally
        {
            MultiplayerSpectateur.Arreter();
        }
        Assert.False(MultiplayerSpectateur.Connecte);
    }

    [Fact]
    public void Sans_spectateur_connecte_le_compte_des_invites_ne_change_pas()
    {
        Assert.False(MultiplayerSpectateur.Connecte);
        Assert.Equal(3, MultiplayerSpectateur.Corriger(() => 3)());
        Assert.Null(MultiplayerSpectateur.Corriger(() => null)());
    }

    [Fact]
    public void Le_direct_du_spectateur_a_une_case_par_console_dans_l_ordre_des_places()
    {
        var mp = new MultiplayerPartieDeLHote("r1", "gba/mario-kart-super-circuit", "Mario Kart", "gba",
            [new(2, "B", null, null), new(1, "A", null, null), new(3, "C", null, null)], "course", 1, null, 2, 2);
        var grille = DirectPartageService.GrilleDuDirect(mp, duSpectateur: true);
        Assert.Equal((2, 2), (grille!.Value.Colonnes, grille.Value.Lignes));
        Assert.Equal([("A", 1), ("B", 2), ("C", 3)], grille.Value.Cases);
        // Le film de l'hote (sa console seule) n'a pas de grille.
        Assert.Null(DirectPartageService.GrilleDuDirect(mp, duSpectateur: false));
    }
}
