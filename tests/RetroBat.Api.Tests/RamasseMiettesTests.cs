using System.Text.Json;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE RAMASSE-MIETTES DE L'API (brief du 13/11, point 1.2) : mode poste de travail, concurrent. Le SDK Web met le mode serveur
/// par defaut (un tas par coeur) ; la publication en un seul fichier embarque ce runtimeconfig dans l'exe, un fichier pose a
/// cote est ignore.
/// </summary>
public class RamasseMiettesTests
{
    [Fact]
    public void L_api_tourne_en_mode_poste_de_travail_et_concurrent()
    {
        var chemin = Path.Combine(AppContext.BaseDirectory, "RetroBat.Api.runtimeconfig.json");
        Assert.True(File.Exists(chemin), "runtimeconfig de l'API introuvable a cote des tests : " + chemin);
        using var json = JsonDocument.Parse(File.ReadAllText(chemin));
        var proprietes = json.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.False(proprietes.GetProperty("System.GC.Server").GetBoolean());
        Assert.True(proprietes.GetProperty("System.GC.Concurrent").GetBoolean());
    }
}
