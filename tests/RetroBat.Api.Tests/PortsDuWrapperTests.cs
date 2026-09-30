using RetroBat.Providers.RetroArchWrapper;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le wrapper 0.340 dit, une fois par seconde et seulement a plusieurs joueurs, combien d'appuis
/// chaque port a recus (1CC MULTI) : la borne y retrouve son port en les rapprochant de son panel.
/// </summary>
public class PortsDuWrapperTests
{
    [Fact]
    public void Les_appuis_de_chaque_port_se_lisent_dans_l_ordre()
    {
        Assert.Equal([3, 5, 0, 0], RetroArchWrapperProvider.LignePorts("{\"ms\":61234,\"presses\":[3,5,0,0]}\n")!);
    }

    [Theory]
    [InlineData("{\"ms\":1,\"presses\":[1,2,3]}")]
    [InlineData("{\"ms\":1,\"presses\":[1,2,3,4,5]}")]
    [InlineData("{\"ms\":1,\"presses\":[1,-2,3,4]}")]
    [InlineData("{\"ms\":1}")]
    [InlineData("pas du json")]
    public void Une_ligne_illisible_ne_donne_rien(string ligne)
    {
        Assert.Null(RetroArchWrapperProvider.LignePorts(ligne));
    }
}
