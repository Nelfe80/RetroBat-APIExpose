using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La triche integree au jeu (CDC Demandes v1.2, D3, 31/10/2026) : une ligne LEVEL_CHEAT_ACTIVATED du .MEM, emise avec une
/// valeur non nulle, marque la partie ; le passeport porte cheats = true.
/// </summary>
public class TricheDuJeuTests
{
    [Fact]
    public void Une_triche_integree_au_jeu_se_reconnait_a_sa_ligne_non_nulle()
    {
        // CDC Demandes v1.2, D3 (31/10) : le mode debug de Sonic 1, 0xFFFB = 1 au chargement du niveau.
        JsonElement Signal(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Assert.Equal(1, NelfePlayScoringReporter.ValeurDeTricheDuJeu(Signal(
            "{\"signal\":{\"Name\":\"LEVEL_CHEAT_ACTIVATED\",\"Address\":\"0xFFFB\",\"Value\":1}}")));
        Assert.Null(NelfePlayScoringReporter.ValeurDeTricheDuJeu(Signal(
            "{\"signal\":{\"Name\":\"LEVEL_CHEAT_ACTIVATED\",\"Address\":\"0xFFFB\",\"Value\":0}}")));
        Assert.Null(NelfePlayScoringReporter.ValeurDeTricheDuJeu(Signal(
            "{\"signal\":{\"Name\":\"LOSE_LIFE\",\"Address\":\"0xFE12\",\"Value\":1}}")));
        Assert.Null(NelfePlayScoringReporter.ValeurDeTricheDuJeu(Signal("{\"score\":120}")));
    }
}
