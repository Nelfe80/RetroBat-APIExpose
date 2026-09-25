using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Sous le cœur libretro MAME, le pont Lua mesure et le wrapper tourne aussi : une partie de Metal
/// Slug 3 a produit deux sessions de fin le 25 septembre 2026, arrivées à 33 ms d'écart. Soumises
/// toutes deux, elles pouvaient doubler un score. La session du wrapper cède au pont Lua, y compris
/// juste APRÈS la fin de la session Lua, puisque l'ordre d'arrivée n'est pas garanti.
/// </summary>
public class ArbitrageSessionTests
{
    private const string Mem = @"E:\RetroBat\plugins\APIExpose\resources\ram\arcade\metal-slug-3.MEM";

    [Fact]
    public void Pendant_la_session_Lua_la_session_du_wrapper_cede()
    {
        var a = new IngameSourceArbitrationService();
        a.MarkMameLuaSessionStarted("arcade", "metal-slug-3", Mem);

        Assert.True(a.ShouldSuppressRetroArchWrapperSession("arcade", "metal-slug-3", Mem));
    }

    [Fact]
    public void Juste_apres_la_fin_de_la_session_Lua_la_session_du_wrapper_cede_encore()
    {
        // LE CAS DE LA COURSE : la session Lua s'est refermée quelques millisecondes avant que
        // la session de fin du wrapper n'arrive.
        var a = new IngameSourceArbitrationService();
        a.MarkMameLuaSessionStarted("arcade", "metal-slug-3", Mem);
        a.MarkMameLuaSessionStopped("arcade", "metal-slug-3", Mem);

        Assert.False(a.ShouldSuppressRetroArchWrapper("arcade", "metal-slug-3", Mem));   // les signaux, eux, repassent
        Assert.True(a.ShouldSuppressRetroArchWrapperSession("arcade", "metal-slug-3", Mem));
    }

    [Fact]
    public void Un_autre_jeu_juste_apres_garde_sa_session()
    {
        // Une partie FBNeo courte lancée juste après une partie MAME ne doit pas perdre sa session.
        var a = new IngameSourceArbitrationService();
        a.MarkMameLuaSessionStarted("arcade", "metal-slug-3", Mem);
        a.MarkMameLuaSessionStopped("arcade", "metal-slug-3", Mem);

        Assert.False(a.ShouldSuppressRetroArchWrapperSession("arcade", "19xx-the-war-against-destiny",
            @"E:\RetroBat\plugins\APIExpose\resources\ram\arcade\19xx-the-war-against-destiny.MEM"));
    }

    [Fact]
    public void Sans_pont_Lua_la_session_du_wrapper_passe()
    {
        // FBNeo, Genesis Plus GX... : le wrapper est le seul pont, sa session est la seule.
        var a = new IngameSourceArbitrationService();

        Assert.False(a.ShouldSuppressRetroArchWrapperSession("arcade", "metal-slug-3", Mem));
    }
}
