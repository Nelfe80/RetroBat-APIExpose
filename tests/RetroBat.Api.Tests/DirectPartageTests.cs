using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le direct partage (CDC video V2.1, §5.2) : ce que la borne passe a l'exe.</summary>
public sealed class DirectPartageTests
{
    [Fact]
    public void Un_clip_du_data_pack_est_un_nom_de_mp4_jamais_un_chemin()
    {
        Assert.Equal(Path.Combine("R", "resources", "live", "NelfePlayTV.mp4"), DirectPartageService.CheminDuClip("R", "NelfePlayTV.mp4"));
        Assert.Null(DirectPartageService.CheminDuClip("R", @"..\..\autre.mp4"));
        Assert.Null(DirectPartageService.CheminDuClip("R", "C:/clip.mp4"));
        Assert.Null(DirectPartageService.CheminDuClip("R", "clip.exe"));
        Assert.Null(DirectPartageService.CheminDuClip("R", null));
    }

    [Fact]
    public void Sans_empreinte_le_clip_ne_part_pas()
    {
        var avec = DirectPartageService.OptionsDuPassage(3000, "c.json", "clip.mp4", "ABC", "b.json");
        Assert.Equal(["--debit", "3000", "--commande", "c.json", "--attente-max", "90", "--bandeau", "b.json", "--clip", "clip.mp4",
            "--clip-sha256", "ABC"], avec);
        var sans = DirectPartageService.OptionsDuPassage(3000, "c.json", "clip.mp4", " ", "b.json");
        Assert.DoesNotContain("--clip", sans);
        // Le debit reste dans des bornes raisonnables.
        Assert.Equal("9000", DirectPartageService.OptionsDuPassage(50_000, "c", null, null, "b")[1]);
    }

    [Fact]
    public void Le_record_a_battre_se_lit_sur_la_bande()
    {
        Assert.Equal("RECORD 523 400 · player", DirectPartageService.TexteDuRecord(523_400, "player"));
        Assert.Equal("RECORD 900", DirectPartageService.TexteDuRecord(900, null));
        Assert.Equal("", DirectPartageService.TexteDuRecord(null, "x"));
        Assert.Equal("", DirectPartageService.TexteDuRecord(0, "x"));
    }

    [Fact]
    public void Une_alert_ne_joue_que_les_clips_presents_et_accompagnes_de_leur_empreinte()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfe-clips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(racine, "resources", "live"));
        File.WriteAllText(Path.Combine(racine, "resources", "live", "New_Record.mp4"), "x");
        try
        {
            var clips = System.Text.Json.Nodes.JsonNode.Parse("""
                {"record":{"file":"New_Record.mp4","sha256":"AA"},"champion":{"file":"New_Champion.mp4","sha256":"BB"}}
                """) as System.Text.Json.Nodes.JsonObject;
            var (annonce, options) = DirectDuRecordService.Clips(clips, racine);
            Assert.Equal(Path.Combine(racine, "resources", "live", "New_Record.mp4"), annonce);
            // Le clip du champion manque sur la borne : il ne part pas.
            Assert.Equal(["--annonce-sha256", "AA"], options);
            // Sans empreinte, rien.
            var sans = System.Text.Json.Nodes.JsonNode.Parse("""{"record":{"file":"New_Record.mp4"}}""") as System.Text.Json.Nodes.JsonObject;
            Assert.Null(DirectDuRecordService.Clips(sans, racine).Annonce);
            Assert.Null(DirectDuRecordService.Clips(null, racine).Annonce);
        }
        finally
        {
            Directory.Delete(racine, recursive: true);
        }
    }

    [Fact]
    public void Les_applaudissements_ne_partent_qu_avec_leur_empreinte()
    {
        var avec = DirectPartageService.OptionsDuPassage(3000, "c", null, null, "b", "Applause.mp4", "CC");
        Assert.Equal(["--applaudissements", "Applause.mp4", "--applaudissements-sha256", "CC"], avec.Skip(8));
        Assert.DoesNotContain("--applaudissements", DirectPartageService.OptionsDuPassage(3000, "c", null, null, "b", "Applause.mp4", ""));
    }

    [Theory]
    [InlineData(1_200, 1_000L, false, false, true)]
    [InlineData(1_000, 1_000L, false, false, false)]   // egaler n'est pas battre
    [InlineData(1_200, null, false, false, false)]     // pas de record connu (classement vide, contre-la-montre)
    [InlineData(1_200, 0L, false, false, false)]
    [InlineData(1_200, 1_000L, true, false, false)]    // un continue : la partie ne compte plus
    [InlineData(1_200, 1_000L, false, true, false)]    // l'alert a pris le relais et joue les siens
    public void Le_record_battu_pendant_un_passage(long meilleur, long? record, bool nonCertifiable, bool alert, bool attendu)
        => Assert.Equal(attendu, DirectPartageService.RecordBattu(meilleur, record, nonCertifiable, alert));

    [Theory]
    [InlineData("1cc", "1CC")]
    [InlineData("1cc-power-up", "1CC POWER-UP")]
    [InlineData("1cc-original", "1CC ORIGINAL")]
    public void La_bande_dit_le_mode_comme_le_classement(string regle, string attendu)
        => Assert.Equal(attendu, DirectPartageService.LibelleDuMode(regle));

    [Fact]
    public void La_commande_dit_a_l_exe_si_le_record_est_tombe()
    {
        // Le format que lit CarteASuivre dans l'exe du direct (0.5.6).
        Assert.Equal("""{"fin":true,"arret":false,"suivant":{"joueur":"player","jeu":"1942","record":false},"recordBattu":true,"relance":false}""",
            DirectPartageService.CommandeJson(true, false, "player", "1942", true, false));
        Assert.Equal("""{"fin":true,"arret":true,"suivant":null,"recordBattu":false,"relance":true}""",
            DirectPartageService.CommandeJson(true, true, null, null, false, true));
    }

    [Theory]
    [InlineData(45, null, false, false, true)]          // un depart rate, personne d'annonce
    [InlineData(45, "player", false, false, true)]      // la carte annonce deja sa nouvelle partie
    [InlineData(45, "autre", false, false, false)]      // un autre joueur prend l'antenne
    [InlineData(300, null, false, false, false)]        // une vraie partie : la transition de la chaine
    [InlineData(300, null, true, false, true)]          // une longue partie, mais il a deja relance
    [InlineData(45, null, false, true, false)]          // un direct de record prend le relais
    public void Une_partie_ratee_puis_relancee_finit_sur_try_again(int secondes, string? annonce, bool nouvelle, bool alert, bool attendu)
        => Assert.Equal(attendu, DirectPartageService.EstUneRelance(TimeSpan.FromSeconds(secondes), annonce, "Player", nouvelle, alert));

    // Lorenzolamas, Tetris TGM2 (2026-10-18) : la nouvelle partie tournait deja, le relais arrivait une seconde apres la fin.
    [Fact]
    public void Un_relais_laisse_l_exe_precedent_finir_sur_try_again_avant_l_arret()
    {
        var (finir, arreter) = DirectPartageService.CommandesDuRelais(recordFete: false);
        Assert.Equal("""{"fin":true,"arret":false,"suivant":null,"recordBattu":false,"relance":true}""", finir);
        Assert.Equal("""{"fin":true,"arret":true,"suivant":null,"recordBattu":false,"relance":true}""", arreter);
        Assert.Equal(TimeSpan.FromSeconds(15), DirectPartageService.PatienceDuRelais(false));
        // L'exe relit la commande une fois par seconde : l'arret attend qu'il ait lu la fin.
        Assert.True(DirectPartageService.LectureDeLaCommande > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Un_relais_garde_la_fete_d_un_record_tombe()
    {
        var precedente = DirectPartageService.CommandeJson(true, false, null, null, recordBattu: true, relance: false);
        Assert.True(DirectPartageService.RecordFeteDansLaCommande(precedente));
        Assert.False(DirectPartageService.RecordFeteDansLaCommande(DirectPartageService.CommandeJson(true, false, null, null, false, false)));
        Assert.False(DirectPartageService.RecordFeteDansLaCommande(null));
        Assert.False(DirectPartageService.RecordFeteDansLaCommande("{casse"));
        Assert.Contains("\"recordBattu\":true", DirectPartageService.CommandesDuRelais(recordFete: true).Finir);
        Assert.Equal(TimeSpan.FromSeconds(45), DirectPartageService.PatienceDuRelais(true));
    }

    [Fact]
    public void La_carte_de_fin_dit_RECORD_ALERT_quand_la_suivante_approche_le_record()
        => Assert.Contains("\"record\":true", DirectPartageService.CommandeJson(true, false, "player", "1942", false, false, true));

    [Fact]
    public void Le_champion_et_l_annonce_d_une_greffe_partent_avec_leur_empreinte()
    {
        var o = DirectPartageService.OptionsDuPassage(3000, "c", null, null, "b", null, null, null, null, "New_Champion.mp4", "CC", "AA");
        Assert.Equal(["--champion", "New_Champion.mp4", "--champion-sha256", "CC", "--annonce-sha256", "AA"], o.Skip(8));
        Assert.DoesNotContain("--champion", DirectPartageService.OptionsDuPassage(3000, "c", null, null, "b", null, null, null, null, "New_Champion.mp4", " "));
    }

    [Fact]
    public void Try_again_ne_part_qu_avec_son_empreinte()
    {
        var avec = DirectPartageService.OptionsDuPassage(3000, "c", null, null, "b", null, null, "TryAgain.mp4", "EE");
        Assert.Equal(["--relance", "TryAgain.mp4", "--relance-sha256", "EE"], avec.Skip(8));
        Assert.DoesNotContain("--relance", DirectPartageService.OptionsDuPassage(3000, "c", null, null, "b", null, null, "TryAgain.mp4", " "));
    }

    [Fact]
    public void Une_alert_applaudit_quand_le_clip_est_sur_la_borne()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfe-clips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(racine, "resources", "live"));
        File.WriteAllText(Path.Combine(racine, "resources", "live", "Applause.mp4"), "x");
        try
        {
            var clips = System.Text.Json.Nodes.JsonNode.Parse("""{"applause":{"file":"Applause.mp4","sha256":"DD"}}""")
                as System.Text.Json.Nodes.JsonObject;
            Assert.Equal(["--applaudissements", Path.Combine(racine, "resources", "live", "Applause.mp4"), "--applaudissements-sha256", "DD"],
                DirectDuRecordService.Clips(clips, racine).Options);
        }
        finally
        {
            Directory.Delete(racine, recursive: true);
        }
    }

    [Fact]
    public void Le_direct_multiplayer_met_le_joueur_1_a_gauche_et_le_joueur_2_a_droite()
    {
        var places = new[]
        {
            new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(2, "ANOTHERKIFF", "?", 1031),
            new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(1, "LORENZOLAMAS", "C", 1112),
        };
        Assert.Equal(("LORENZOLAMAS · C", "ANOTHERKIFF · ?"), DirectPartageService.CotesDeLaBande(places));
        // A quatre, chaque cote garde ses deux places ; un jeu sans classement n'a pas de lettre.
        var quatre = new[]
        {
            new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(1, "A", null, null),
            new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(2, "B", null, null),
            new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(3, "C", null, null),
            new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(4, "D", null, null),
        };
        Assert.Equal(("A & C", "B & D"), DirectPartageService.CotesDeLaBande(quatre));
    }

    [Fact]
    public void Le_jeu_d_un_direct_multiplayer_ne_se_confond_pas_avec_un_classement_de_score()
    {
        Assert.Equal("mp.arcade.sf2ce", DirectPartageService.RomGroupMultiplayer("arcade/sf2ce"));
        Assert.Equal("mp.gba.doom-ii", DirectPartageService.RomGroupMultiplayer(" GBA/Doom-II "));
        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,189}$", DirectPartageService.RomGroupMultiplayer("snes/smk"));
    }

    [Fact]
    public void L_hote_annonce_sa_partie_multiplayer_avec_chaque_place()
    {
        var room = new RetroBat.Api.Multiplayer.MultiplayerRoom
        {
            Room = "r1",
            Players =
            [
                new() { Handle = "ak", Pseudo = "", Seat = 2, Rating = new() { Value = 1031, Provisional = true } },
                new() { Handle = "lorenzo", Pseudo = "LORENZOLAMAS", Seat = 1, Rating = new() { Value = 1112 } },
                new() { Handle = "spectateur", Pseudo = "VU" },
            ],
        };
        var jeu = new RetroBat.Api.Multiplayer.MultiplayerInstalledGame(
            new RetroBat.Api.Multiplayer.MultiplayerGame { Id = "arcade/sf2ce", Name = "Street Fighter II' Champion Edition", System = "arcade" },
            "sf2ce.zip", null, "sf2ce");
        var partie = RetroBat.Api.Multiplayer.MultiplayerLaunchService.PartieDeLHote(room, jeu);
        Assert.Equal(("r1", "arcade/sf2ce", "arcade"), (partie.Room, partie.Game, partie.System));
        Assert.Equal(
            [new(1, "LORENZOLAMAS", "C", 1112), new RetroBat.Api.Multiplayer.MultiplayerPlaceDuDirect(2, "ak", "?", 1031)],
            partie.Players);
        // Le bus passe l'objet tel quel ; sa forme JSON se relit aussi.
        Assert.Same(partie, DirectPartageService.PartieMultiplayer(partie));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(partie);
        Assert.Equal("r1", DirectPartageService.PartieMultiplayer(json)?.Room);
        Assert.Null(DirectPartageService.PartieMultiplayer(new { Room = "" }));
    }

    [Fact]
    public void Les_options_du_passage_suivent_les_arguments_du_direct()
    {
        var args = LanceurDuDirect.Arguments("film.mkv", 0, options: ["--debit", "3000"]);
        Assert.Equal(["--film", "film.mkv", "--adresse-stdin", "--rotation", "0", "--suivre", "--debit", "3000"], args);
    }
}
