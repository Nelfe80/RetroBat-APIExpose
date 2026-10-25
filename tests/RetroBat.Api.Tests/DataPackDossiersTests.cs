using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'arbre du Data Pack deja lu ne sert que pour les memes dossiers (25/10) : une borne neuve synchronisee d'abord par une
/// version qui ne suivait pas « multiplayer » ne recevait jamais le catalogue, l'arbre n'ayant pas bouge depuis.
/// </summary>
public class DataPackDossiersTests
{
    private static HashSet<string> Actuels(params string[] dossiers) => new(dossiers, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Un_etat_d_avant_la_liste_des_dossiers_relit_l_arbre()
        => Assert.False(DataPackPaths.MemesDossiers(null, Actuels("ram", "multiplayer")));

    [Fact]
    public void Un_dossier_de_plus_relit_l_arbre()
        => Assert.False(DataPackPaths.MemesDossiers(["ram", "theme"], Actuels("ram", "theme", "multiplayer")));

    [Fact]
    public void Un_dossier_de_moins_relit_l_arbre()
        => Assert.False(DataPackPaths.MemesDossiers(["ram", "theme", "live"], Actuels("ram", "theme")));

    [Fact]
    public void Les_memes_dossiers_gardent_l_arbre_lu_quel_que_soit_l_ordre_ou_la_casse()
        => Assert.True(DataPackPaths.MemesDossiers(["Multiplayer", "ram"], Actuels("ram", "multiplayer")));
}
