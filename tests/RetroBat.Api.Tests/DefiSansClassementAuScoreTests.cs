using System.Text.Json;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// UN DEFI SUR UN JEU OUVERT AUX SEULS SPEEDRUNS (18/11, support Old AF Gamer) : `scores/profile` repond « pas ouvert » pour Mega
/// Man 2, SMB3, Super Mario World ou Super Metroid, et chaque temps etait retire sans envoi. Le profil de l'epreuve, compose
/// depuis l'index des defis, porte ce que l'assemblage du passeport lit (BuildPassport) et ce que le site attend.
/// </summary>
public class DefiSansClassementAuScoreTests
{
    [Fact]
    public void Le_profil_de_l_epreuve_porte_ce_que_le_passeport_lit()
    {
        var entree = new EntreeDeLIndex("ch-smw-yi1-v1", new string('b', 64), new string('c', 64));
        var p = PasseportDeDefi.ProfilSansClassementAuScore("snes", "super-mario-world", entree, 1);

        // Les trois champs que BuildPassport lit avec GetProperty (une absence lancerait) :
        Assert.Equal("ch-smw-yi1-v1", p.GetProperty("ruleset").GetString());
        Assert.Equal(1, p.GetProperty("profile_version").GetInt64());
        Assert.Equal(new string('b', 64), p.GetProperty("profile_document_sha256").GetString());
        Assert.Equal("libretro", p.GetProperty("engine").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("manifest_commit").ValueKind);
        // Un temps, plus court gagne : jamais un score.
        Assert.Equal("time", p.GetProperty("metric").GetProperty("type").GetString());
        Assert.Equal("lower_better", p.GetProperty("metric").GetProperty("ranking_direction").GetString());
        Assert.Equal("none", p.GetProperty("bios").GetProperty("mode").GetString());
        // Ni mode, ni difficulte, ni NVRAM epinglee : rien a lire au-dela.
        Assert.False(p.TryGetProperty("mode", out _));
        Assert.False(p.TryGetProperty("difficulty", out _));
        Assert.False(p.TryGetProperty("nvram_pins", out _));
    }
}
