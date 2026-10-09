using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Ou RetroArch ecrit ses captures (2026-10-10) : la photo d'un record ne se gardait pas chez un
/// joueur, en silence. Le dossier configure, le « : » de RetroArch, le rangement par contenu.
/// </summary>
public class CapturesRetroArchTests
{
    private const string Ra = @"F:\RetroBat\emulators\retroarch";

    [Theory]
    [InlineData(@"screenshot_directory = ""F:\RetroBat\screenshots""", @"F:\RetroBat\screenshots")]
    [InlineData(@"screenshot_directory = "":\screenshots""", @"F:\RetroBat\emulators\retroarch\screenshots")]
    [InlineData(@"screenshot_directory = "":\""", @"F:\RetroBat\emulators\retroarch")]
    [InlineData(@"screenshot_directory=""D:\Captures""", @"D:\Captures")]
    public void Le_dossier_configure_est_lu(string ligne, string attendu)
    {
        Assert.Equal(attendu, CapturesRetroArch.LireDossier(new[] { "video_driver = \"gl\"", ligne }, Ra));
    }

    [Theory]
    [InlineData(@"screenshot_directory = ""default""")]
    [InlineData(@"screenshot_directory = """"")]
    [InlineData(@"screenshots_in_content_dir = ""false""")]
    [InlineData(@"# screenshot_directory = ""F:\ailleurs""")]
    public void Sans_dossier_configure_rien_n_est_invente(string ligne)
    {
        Assert.Null(CapturesRetroArch.LireDossier(new[] { ligne }, Ra));
    }

    [Fact]
    public void Une_capture_rangee_par_contenu_est_retrouvee()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfe-captures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var avant = DateTime.UtcNow.AddSeconds(-1);
            var ancienne = Path.Combine(racine, "ancienne.png");
            var parJeu = Path.Combine(racine, "mastersystem", "Alex Kidd-261010-120000.png");
            Directory.CreateDirectory(Path.GetDirectoryName(parJeu)!);
            File.WriteAllBytes(ancienne, new byte[] { 1 });
            File.SetLastWriteTimeUtc(ancienne, avant.AddMinutes(-5));
            File.WriteAllBytes(parJeu, new byte[] { 1, 2, 3 });

            Assert.Equal(parJeu, CapturesRetroArch.PlusRecente(racine, avant)?.FullName);
        }
        finally
        {
            try { Directory.Delete(racine, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Un_dossier_encore_absent_ne_trouve_rien_sans_erreur()
    {
        var absent = Path.Combine(Path.GetTempPath(), "nelfe-captures-absent-" + Guid.NewGuid().ToString("N"));
        Assert.Null(CapturesRetroArch.PlusRecente(absent, DateTime.UtcNow.AddSeconds(-1)));
    }
}
