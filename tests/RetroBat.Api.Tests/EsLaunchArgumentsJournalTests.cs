using System.Text;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le journal d'ES se lit par la fin (2026-09-30) : 23 Mo lus en entier a chaque lancement direct.
/// Des blocs minuscules font tomber lignes et caracteres accentues a cheval sur deux blocs.
/// </summary>
public sealed class EsLaunchArgumentsJournalTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), "es_log_" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose()
    {
        if (File.Exists(_chemin))
        {
            File.Delete(_chemin);
        }
    }

    private const string Premier = "lvl2: \"E:\\RetroBat\\emulatorLauncher.exe\" -p1index 0 -p1name \"Manette é\" -system fbneo -rom \"E:\\RetroBat\\roms\\fbneo\\1942.zip\"";
    private const string Second = "lvl2: \"E:\\RetroBat\\emulatorLauncher.exe\" -p1index 1 -p1name \"Borne à deux\" -system mame -rom \"E:\\RetroBat\\roms\\mame\\19xx.zip\"";

    private void Ecrire(string contenu) => File.WriteAllText(_chemin, contenu, new UTF8Encoding(false));

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64 * 1024)]
    public void LesLancementsSortentDuDernierAuPremier(int bloc)
    {
        Ecrire("demarrage d'ES\r\n" + Premier + "\r\nune ligne sans lancement, accentuée\r\n" + Second + "\r\nfin\r\n");

        var lignes = EsLaunchArguments.LancementsDepuisLaFin(_chemin, bloc).ToList();

        Assert.Equal([Second, Premier], lignes);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(64 * 1024)]
    public void LaPremiereLigneEtUneFinSansSautDeLigneComptent(int bloc)
    {
        Ecrire(Premier + "\n" + Second);

        var lignes = EsLaunchArguments.LancementsDepuisLaFin(_chemin, bloc).ToList();

        Assert.Equal([Second, Premier], lignes);
    }

    [Fact]
    public void UnJournalAbsentOuVideNeDonneRien()
    {
        Assert.Empty(EsLaunchArguments.LancementsDepuisLaFin(_chemin + ".absent"));
        Ecrire(string.Empty);
        Assert.Empty(EsLaunchArguments.LancementsDepuisLaFin(_chemin));
    }

    [Fact]
    public void LesManettesDuDernierLancementSontCellesDeLaDerniereLigne()
    {
        Ecrire(Premier + "\n" + Second + "\n");

        var dernier = EsLaunchArguments.LancementsDepuisLaFin(_chemin, 11).First();

        Assert.Equal("-p1index 1 -p1name \"Borne à deux\"", EsLaunchArguments.ManettesDe(dernier));
    }
}
