using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le film de RetroArch pendant la partie (20/11) : assez leger pour ne pas ralentir le jeu, assez fidele pour les records.</summary>
public class ReglagesDuFilmTests
{
    [Fact]
    public void Le_film_est_en_x264_veryfast_4_4_4_crf_14()
    {
        var r = ReplayCaptureService.ReglagesDuFilm;
        Assert.Contains("vcodec = \"libx264\"", r);
        Assert.Contains("video_preset = \"veryfast\"", r);
        Assert.Contains("video_crf = \"14\"", r);
        // 4:4:4 : la chaine aval (LiveStreaming, films des records) relit la couleur de chaque pixel ; le 4:2:0 abime les sprites.
        Assert.Contains("pix_fmt = \"yuv444p\"", r);
        Assert.DoesNotContain("yuv420", r);
        Assert.Contains("scale_factor = \"1\"", r);
    }
}
