using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La trame des lectures de score suit les evenements memoire. Elle ne venait que de retroarch.score,
/// qui n'en porte pas : toutes les lectures restaient a la trame 0, la coupure 1CC a la derniere vie
/// perdue ne tombait jamais entre deux lectures, et une partie de 19xx continuee a ete certifiee avec
/// les points du continue (2026-09-25).
/// </summary>
public class ScoringTrameMemoireTests
{
    [Fact]
    public void La_trame_du_signal_memoire_est_lue()
    {
        using var doc = JsonDocument.Parse("""{"Source":"retroarch.wrapper.pipe","Signal":{"Name":"SCORE_STATE","Address":"0X10ED18","Frame":2165}}""");

        Assert.Equal(2165L, NelfePlayScoringReporter.TrameDuSignal(doc.RootElement));
    }

    [Fact]
    public void Le_pont_Lua_sans_trame_ne_change_rien()
    {
        using var doc = JsonDocument.Parse("""{"Source":"mame.lua.ingame","signal":{"Name":"LOSE_LIFE","Address":"0x2BA"}}""");

        Assert.Null(NelfePlayScoringReporter.TrameDuSignal(doc.RootElement));
    }

    [Fact]
    public void Avec_des_trames_justes_le_continue_ne_compte_plus()
    {
        // La partie de 19xx du 25/09, reconstituee : 5 900 avant la derniere vie perdue (trame 2165),
        // puis un continue, le score reporte, et 9 000 a la fin. Le 1CC vaut 5 900.
        var traj = new List<(long frame, long total)>
        {
            (300, 100), (900, 2000), (1500, 5400), (2100, 5900),
            (2400, 8600), (2600, 9000),
        };

        var meilleur = NelfePlayScoringReporter.SelectBestRun(traj, [2165]);

        Assert.Equal(5900, meilleur[^1].total);
    }
}
