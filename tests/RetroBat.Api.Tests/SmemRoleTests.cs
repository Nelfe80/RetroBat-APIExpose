using System.Text.Json;
using RetroBat.Api.Deck;
using Xunit;

namespace RetroBat.Api.Tests;

public sealed class SmemRoleTests
{
    [Theory]
    [InlineData("players.1.lives", "lives")]
    [InlineData("players.2.resources.lives", "lives")]
    [InlineData("p1.lives", "lives")]
    [InlineData("players.1.bombs", "bombs")]
    [InlineData("players.1.resources.bombs", "bombs")]
    [InlineData("p2.bombs", "bombs")]
    [InlineData("bombs", "bombs")]
    [InlineData("players.1.ammo", "ammo")]
    [InlineData("players.1.health", "health")]
    [InlineData("players.1.power.level", "power")]
    [InlineData("boss.hp", "boss_hp")]
    [InlineData("stage", "stage")]
    [InlineData("players.1.bombs.used", null)]
    [InlineData("players.1.health.max", null)]
    [InlineData("boss.hp_max", null)]
    [InlineData("boss.phase", null)]
    [InlineData("players.1.game.daioujou.bomb_capacity", null)]
    [InlineData("players.1.game.sonic_the_hedgehog.rings", null)]
    [InlineData("stage.progress", null)]
    [InlineData("rank", null)]
    [InlineData("", null)]
    public void Le_role_se_deduit_de_la_fin_de_l_identifiant(string id, string? attendu)
    {
        Assert.Equal(attendu, SmemRole.Depuis(id));
    }

    [Fact]
    public void Le_role_part_dans_le_json_du_signal_a_cote_de_l_identifiant_d_origine()
    {
        var s = new DeckSemanticSignal("players.1.resources.bombs", 1, "counter", null, "Bombs", null,
            Array.Empty<DeckSemanticRef>(), Array.Empty<DeckSemanticValue>(), new DeckSemanticScale(1, 1, 0),
            0, 9, 3, "exact", true);
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"id\":\"players.1.resources.bombs\"", json);
        Assert.Contains("\"role\":\"bombs\"", json);
    }
}
