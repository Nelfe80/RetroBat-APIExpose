using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Ce que la borne annonce comme coeur de lancement part dans un en-tete HTTP. Un en-tete ne
/// tolere ni accent, ni saut de ligne, ni longueur libre : une valeur mal formee ferait refuser
/// TOUT le releve de l'agent, donc les installations et le suivi avec.
/// </summary>
public class CabinetStateCoresTests
{
    [Fact]
    public void La_liste_est_triee_et_lisible()
    {
        CabinetState.NoterCoeurs(new Dictionary<string, string>
        {
            ["ms-pac-man"] = "fbneo",
            ["double-dragon"] = "fbneo",
            ["sonic-the-hedgehog"] = "genesis_plus_gx",
        });

        Assert.Equal("double-dragon=fbneo,ms-pac-man=fbneo,sonic-the-hedgehog=genesis_plus_gx",
            CabinetState.Coeurs);
    }

    [Fact]
    public void Ce_qui_ne_passerait_pas_dans_un_en_tete_est_ecarte()
    {
        CabinetState.NoterCoeurs(new Dictionary<string, string>
        {
            ["jeu accentue é"] = "fbneo",
            ["saut\r\nde-ligne"] = "fbneo",
            ["ok-jeu"] = "fbneo",
            ["sans-coeur"] = "",
        });

        Assert.Equal("ok-jeu=fbneo", CabinetState.Coeurs);
    }

    [Fact]
    public void Une_liste_trop_longue_est_coupee_et_non_refusee()
    {
        var beaucoup = new Dictionary<string, string>();
        for (var i = 0; i < 100; i++)
        {
            beaucoup["jeu-numero-" + i.ToString("D3")] = "fbneo";
        }

        CabinetState.NoterCoeurs(beaucoup);

        Assert.True(CabinetState.Coeurs.Length <= 480, CabinetState.Coeurs.Length.ToString());
        Assert.StartsWith("jeu-numero-000=fbneo,", CabinetState.Coeurs);
    }

    [Fact]
    public void Aucun_jeu_ouvert_donne_une_chaine_vide()
    {
        CabinetState.NoterCoeurs(new Dictionary<string, string>());

        Assert.Equal("", CabinetState.Coeurs);
    }
}
