using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 23/11 : le film d'une epreuve relancee se perdait (<c>File.Move</c> sur un MKV que RetroArch tient encore ouvert, sans
/// nouvelle tentative : « Capture : arret en echec »). Le deplacement reessaie, comme celui des rencontres MULTIPLAYER.
/// </summary>
public sealed class FilmRelanceEpreuveTests
{
    [Fact]
    public async Task Un_film_tenu_ouvert_se_deplace_des_que_RetroArch_le_lache()
    {
        var essais = 0;
        var attentes = 0;
        var ok = await ReplayCaptureService.DeplacerAvecEssaisAsync(
            (_, _) => { if (++essais < 3) throw new IOException("tenu"); }, "a", "b", 5, TimeSpan.Zero, _ => { attentes++; return Task.CompletedTask; });
        Assert.True(ok);
        Assert.Equal(3, essais);
        Assert.Equal(2, attentes);
    }

    [Fact]
    public async Task Un_film_toujours_tenu_rend_faux_apres_le_nombre_d_essais_sans_lever()
    {
        var essais = 0;
        var ok = await ReplayCaptureService.DeplacerAvecEssaisAsync(
            (_, _) => { essais++; throw new IOException("tenu"); }, "a", "b", 4, TimeSpan.Zero, _ => Task.CompletedTask);
        Assert.False(ok);
        Assert.Equal(4, essais);
    }

    [Fact]
    public async Task Le_film_de_la_partie_precedente_survit_a_plusieurs_relances_rapides()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "film_relance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var rangees = new List<Task<bool>>();
            var tenus = new List<FileStream>();
            for (var i = 0; i < 3; i++)
            {
                var src = Path.Combine(dossier, "ra_" + i + ".mkv");
                var flux = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.None); // RetroArch tient le film
                flux.Write(new byte[] { (byte)i, 1, 2 });
                flux.Flush();
                tenus.Add(flux);
                var dst = Path.Combine(dossier, "rp_" + i + ".mkv");
                var indice = i;
                rangees.Add(ReplayCaptureService.DeplacerAvecEssaisAsync(File.Move, src, dst, 50, TimeSpan.FromMilliseconds(20),
                    async d => { await Task.Delay(d); if (indice < tenus.Count) { tenus[indice].Dispose(); } }));
            }
            Assert.All(await Task.WhenAll(rangees), Assert.True);
            for (var i = 0; i < 3; i++)
            {
                Assert.False(File.Exists(Path.Combine(dossier, "ra_" + i + ".mkv")));
                Assert.Equal((byte)i, File.ReadAllBytes(Path.Combine(dossier, "rp_" + i + ".mkv"))[0]);
            }
        }
        finally { try { Directory.Delete(dossier, true); } catch (IOException) { } }
    }
}
