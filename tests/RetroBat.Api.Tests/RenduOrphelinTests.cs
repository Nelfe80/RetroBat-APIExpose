using System.Diagnostics;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le RetroArch d'un rendu survit a un redemarrage de l'API, sans personne pour l'arreter (30/10/2026). La trace le
/// reconnait au demarrage suivant, et ne vise jamais un autre programme qui aurait repris son numero.
/// </summary>
public class RenduOrphelinTests : IDisposable
{
    private readonly DirectoryInfo _dossier = Directory.CreateTempSubdirectory("rendu-orphelin-");
    private string Fichier => Path.Combine(_dossier.FullName, "state", "rendu-en-cours.txt");

    public void Dispose()
    {
        try { _dossier.Delete(true); } catch (IOException) { }
    }

    private static Process UnProcessus() => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;

    [Fact]
    public void Le_rendu_note_s_arrete_au_demarrage_suivant()
    {
        using var rendu = UnProcessus();
        EmulatorForeground.EcrireLaTrace(Fichier, rendu.Id);

        Assert.True(EmulatorForeground.ArreterLeRenduOrphelin(Fichier));

        Assert.True(rendu.WaitForExit(5000));
        Assert.False(File.Exists(Fichier));
    }

    [Fact]
    public void Un_autre_programme_qui_a_repris_le_numero_n_est_pas_touche()
    {
        using var autre = UnProcessus();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Fichier)!);
            File.WriteAllText(Fichier, autre.Id + " " + DateTime.UtcNow.AddHours(-3).Ticks);

            Assert.False(EmulatorForeground.ArreterLeRenduOrphelin(Fichier));

            Assert.False(autre.HasExited);
            Assert.False(File.Exists(Fichier));
        }
        finally
        {
            autre.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Un_rendu_deja_fini_ne_fait_rien()
    {
        using var fini = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true })!;
        fini.WaitForExit();
        Directory.CreateDirectory(Path.GetDirectoryName(Fichier)!);
        File.WriteAllText(Fichier, fini.Id + " 12345");

        Assert.False(EmulatorForeground.ArreterLeRenduOrphelin(Fichier));
        Assert.False(File.Exists(Fichier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas une trace")]
    public void Une_trace_illisible_ne_fait_rien(string contenu)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Fichier)!);
        File.WriteAllText(Fichier, contenu);

        Assert.False(EmulatorForeground.ArreterLeRenduOrphelin(Fichier));
    }

    [Fact]
    public void Sans_trace_rien_a_faire() => Assert.False(EmulatorForeground.ArreterLeRenduOrphelin(Fichier));
}
