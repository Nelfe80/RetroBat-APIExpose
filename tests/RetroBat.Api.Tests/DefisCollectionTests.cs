using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA COLLECTION DES DEFIS (lot P4) : le croisement entre la collection World Scoring et le catalogue. Le reste (ecriture
/// de la collection, logo, reglage) est le socle deja eprouve par World Scoring et MULTIPLAYER.
/// </summary>
public class DefisCollectionTests
{
    private static readonly Reglement GreenHill1 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json")));

    private const string Rom = @"E:\RetroBat\roms\megadrive\Sonic.zip";

    private static readonly Dictionary<string, string> Sonic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["megadrive/sonic-the-hedgehog"] = Rom,
    };

    [Fact]
    public void Un_jeu_de_world_scoring_avec_un_defi_ouvert_entre_avec_ses_defis()
    {
        var jeu = Assert.Single(DefisCollectionSyncService.Croiser([GreenHill1], Sonic, _ => true));
        Assert.Equal(Rom, jeu.Chemin);
        Assert.Equal(GreenHill1.ChallengeId, Assert.Single(jeu.Defis).ChallengeId);
    }

    /// <summary>World Scoring garde les chemins de Windows, ES donne des barres obliques : c'est le meme fichier (borne, 01/11).</summary>
    [Theory]
    [InlineData("E:/RetroBat/roms/megadrive/Sonic.zip")]
    [InlineData(@"E:\RetroBat\roms\megadrive\Sonic.zip")]
    [InlineData("e:/retrobat/roms/megadrive/sonic.zip")]
    public void Le_jeu_selectionne_dans_ES_retrouve_ses_defis(string cheminDEs)
    {
        var jeux = DefisCollectionSyncService.Croiser([GreenHill1], Sonic, _ => true);
        Assert.Equal(GreenHill1.ChallengeId, Assert.Single(DefisCollectionSyncService.DefisDe(jeux, cheminDEs)).ChallengeId);
    }

    [Fact]
    public void Un_autre_jeu_n_a_aucun_defi()
        => Assert.Empty(DefisCollectionSyncService.DefisDe(DefisCollectionSyncService.Croiser([GreenHill1], Sonic, _ => true), "E:/RetroBat/roms/megadrive/Autre.zip"));

    [Fact]
    public void Un_jeu_absent_de_world_scoring_n_entre_pas()
        => Assert.Empty(DefisCollectionSyncService.Croiser([GreenHill1], new Dictionary<string, string>(), _ => true));

    [Fact]
    public void Un_defi_dont_le_MEM_n_est_pas_celui_de_la_borne_n_est_pas_propose()
        => Assert.Empty(DefisCollectionSyncService.Croiser([GreenHill1], Sonic, _ => false));

    [Fact]
    public void Un_jeu_sans_defi_ouvert_n_entre_pas()
        => Assert.Empty(DefisCollectionSyncService.Croiser([], Sonic, _ => true));
}
