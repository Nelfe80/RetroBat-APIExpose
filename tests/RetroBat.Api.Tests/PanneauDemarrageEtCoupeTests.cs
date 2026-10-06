using System.Drawing;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Paths;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le panneau de classement (2026-10-04) : un pseudo trop long se coupe par trois points (le
/// caractere « … » sortait en carres), et au demarrage de l'API le jeu choisi se lit dans
/// events.ini tant qu'ES n'a rien envoye.
/// </summary>
public sealed class PanneauDemarrageEtCoupeTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), "nelfe-events-" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose()
    {
        try { File.Delete(_fichier); } catch { }
    }

    [Fact]
    public void Un_pseudo_trop_long_se_coupe_par_trois_points()
    {
        using var image = new Bitmap(10, 10);
        using var g = Graphics.FromImage(image);
        using var police = new Font(FontFamily.GenericSansSerif, 20f, GraphicsUnit.Pixel);
        float Mesure(string t) => g.MeasureString(t, police, PointF.Empty, StringFormat.GenericTypographic).Width;

        var largeur = Mesure("PLAYERPLA");
        var coupe = LeaderboardOverlayService.Couper(g, "PLAYERPLAYER", police, largeur);

        Assert.EndsWith("...", coupe);
        Assert.StartsWith("PLAYER", coupe);
        Assert.DoesNotContain('\u2026', coupe);
        Assert.True(Mesure(coupe) <= largeur);
        Assert.Equal("PLAYER", LeaderboardOverlayService.Couper(g, "PLAYER", police, largeur));
    }

    [Fact]
    public void Le_jeu_choisi_se_lit_dans_events_ini()
    {
        File.WriteAllLines(_fichier, new[]
        {
            "event=game-selected",
            "mame E:/RetroBat/roms/mame/altbeast.zip \"Altered Beast (Version 1)\"",
            "timestamp=04/10/2026  9:01:35,84",
        });

        Assert.True(EventsIniFile.TryReadGameSelected(_fichier, DateTime.UtcNow.AddMinutes(-1), out var systeme, out var chemin, out var nom));
        Assert.Equal("mame", systeme);
        Assert.Equal("E:/RetroBat/roms/mame/altbeast.zip", chemin);
        Assert.Equal("Altered Beast (Version 1)", nom);
    }

    [Fact]
    public void Un_fichier_d_avant_le_demarrage_d_ES_ou_un_autre_evenement_ne_dit_rien()
    {
        File.WriteAllLines(_fichier, new[] { "event=game-selected", "fbneo E:/RetroBat/roms/fbneo/1942.zip \"1942\"" });
        Assert.False(EventsIniFile.TryReadGameSelected(_fichier, DateTime.UtcNow.AddMinutes(1), out _, out _, out _));

        File.WriteAllLines(_fichier, new[] { "event=system-selected", "fbneo" });
        Assert.False(EventsIniFile.TryReadGameSelected(_fichier, DateTime.UtcNow.AddMinutes(-1), out _, out _, out _));

        Assert.False(EventsIniFile.TryReadGameSelected(_fichier + ".absent", DateTime.MinValue, out _, out _, out _));
    }
}
