using RetroBat.Api.Replay.Sharing;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le replay d'un score s'inscrit au semis quelques secondes AVANT qu'EmulationStation annonce la fin du jeu : la
/// tentative immediate tombait « en partie » et le replay attendait le tour suivant, jusqu'a cinq minutes (2026-10-10).
/// La fin d'une partie ou d'une lecture relance la file, si des replays y attendent.
/// </summary>
public class ReplaySeedRelanceTests
{
    [Theory]
    [InlineData("ui.game.ended", 1, true)]
    [InlineData("replay.finished", 2, true)]
    [InlineData("ui.game.ended", 0, false)]
    [InlineData("ui.game.started", 1, false)]
    [InlineData("replay.started", 1, false)]
    [InlineData(null, 1, false)]
    public void La_fin_d_une_partie_relance_une_file_non_vide(string? evenement, int enFile, bool attendu)
    {
        Assert.Equal(attendu, ReplaySeedService.Relance(evenement, enFile));
    }
}
