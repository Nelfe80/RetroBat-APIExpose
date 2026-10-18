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
    public void Les_options_du_passage_suivent_les_arguments_du_direct()
    {
        var args = LanceurDuDirect.Arguments("film.mkv", 0, options: ["--debit", "3000"]);
        Assert.Equal(["--film", "film.mkv", "--adresse-stdin", "--rotation", "0", "--suivre", "--debit", "3000"], args);
    }
}
