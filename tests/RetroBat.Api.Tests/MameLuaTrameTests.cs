using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le pont Lua de MAME n'envoyait pas la trame : la coupure 1CC au continue n'agissait pas sous
/// MAME (autonome comme coeur libretro). Depuis le plugin 0.3.2, « VALUE|id|valeur|trame ».
/// </summary>
public class MameLuaTrameTests
{
    [Fact]
    public void Le_plugin_0_3_2_donne_la_trame()
    {
        Assert.True(MameLuaIngameProvider.TryParseValueLine("VALUE|7|2|5308".Split('|'), out var id, out var valeur, out var trame));
        Assert.Equal(7, id);
        Assert.Equal(2, valeur);
        Assert.Equal(5308L, trame);
    }

    [Fact]
    public void Un_plugin_plus_ancien_reste_lu_sans_trame()
    {
        Assert.True(MameLuaIngameProvider.TryParseValueLine("VALUE|7|2".Split('|'), out _, out var valeur, out var trame));
        Assert.Equal(2, valeur);
        Assert.Null(trame);
    }

    [Theory]
    [InlineData("VALUE|7|2|0")]
    [InlineData("VALUE|7|2|abc")]
    public void Une_trame_absente_ou_illisible_ne_bloque_pas_la_valeur(string ligne)
    {
        Assert.True(MameLuaIngameProvider.TryParseValueLine(ligne.Split('|'), out _, out var valeur, out var trame));
        Assert.Equal(2, valeur);
        Assert.Null(trame);
    }

    [Fact]
    public void Une_ligne_sans_valeur_est_ignoree()
    {
        Assert.False(MameLuaIngameProvider.TryParseValueLine("VALUE|7".Split('|'), out _, out _, out _));
        Assert.False(MameLuaIngameProvider.TryParseValueLine("VALUE|x|2|10".Split('|'), out _, out _, out _));
    }

    [Fact]
    public void La_trame_publiee_par_le_pont_arrive_au_rapporteur()
    {
        // La forme exacte du signal publie par MameLuaIngameProvider.PublishRuleAsync.
        var avec = JsonSerializer.SerializeToElement(new { Source = "mame.lua", signal = new { Name = "LOSE_LIFE", Address = "0xFF82EC", Frame = (long?)5308 } });
        var sans = JsonSerializer.SerializeToElement(new { Source = "mame.lua", signal = new { Name = "LOSE_LIFE", Address = "0xFF82EC", Frame = (long?)null } });

        Assert.Equal(5308L, NelfePlayScoringReporter.TrameDuSignal(avec));
        Assert.Null(NelfePlayScoringReporter.TrameDuSignal(sans));
    }
}
