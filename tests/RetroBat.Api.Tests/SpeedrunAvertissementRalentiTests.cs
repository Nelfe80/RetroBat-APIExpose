using Xunit;
using RetroBat.Api.Speedrun;

namespace RetroBat.Api.Tests;

/// <summary>07/11 : entre 97 et 99 % de la vitesse du jeu, le temps compte mais le joueur est prevenu (ecran a 59 Hz).</summary>
public class SpeedrunAvertissementRalentiTests
{
    private static readonly Reglement GreenHill1 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json")));

    private static ResultatTentative Finie(long frames) => new(
        EtatTentative.Finie, null, 1000 + frames, "aligned", new MesureChrono(1000, 1000 + frames, frames, 0), null, frames, null, "ok",
        new Dictionary<string, int>(), new Dictionary<string, int?>(), [], [], 59.922743);

    [Fact]
    public void Un_jeu_a_la_bonne_vitesse_ne_previent_de_rien()
        => Assert.Null(SessionSpeedrun.AvertissementDeRalenti(GreenHill1, Finie(2100), 10_000, 10_000 + 35_045));

    [Fact]
    public void Un_jeu_ralenti_de_1_5_pour_cent_previent_le_joueur()
    {
        var m = SessionSpeedrun.AvertissementDeRalenti(GreenHill1, Finie(2100), 10_000, 10_000 + 35_580);
        Assert.NotNull(m);
        Assert.Equal("speedrun.msg.slow_game", m!.Cle);
        Assert.Equal(("percent", "1.5"), m.Valeurs.Single());
    }

    [Fact]
    public void Sans_horloge_rien()
        => Assert.Null(SessionSpeedrun.AvertissementDeRalenti(GreenHill1, Finie(2100), null, 45_000));
}
