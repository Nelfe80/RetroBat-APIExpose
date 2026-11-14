using RetroBat.Api.Infrastructure;
using Xunit;
using P = RetroBat.Api.Infrastructure.PhysicalMediaWebSocketProjectionService.PassesDeSelection;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// UNE SEULE PROJECTION PAR SELECTION (brief du 13/11, point 1.3) : ES publie la selection deux fois sous la meme sequence ;
/// la seconde passe remplace la premiere encore en cours et ne republie que si son resultat differe.
/// </summary>
public class PassesDeSelectionTests
{
    [Fact]
    public void Une_seconde_passe_de_la_meme_selection_depasse_la_premiere()
    {
        var passes = new P();
        var premiere = passes.Ouvrir(12);
        Assert.False(passes.Depassee(12, premiere));

        var seconde = passes.Ouvrir(12);
        Assert.True(passes.Depassee(12, premiere));
        Assert.False(passes.Depassee(12, seconde));
    }

    [Fact]
    public void Une_selection_plus_ancienne_arrivee_en_retard_ne_depasse_rien()
    {
        var passes = new P();
        var destination = passes.Ouvrir(20);
        var retard = passes.Ouvrir(19);

        Assert.False(passes.Depassee(20, destination));
        // La passe en retard n'est pas « depassee » par ce filtre : c'est le filtre par sequence qui l'ecarte.
        Assert.False(passes.Depassee(19, retard));
    }

    [Fact]
    public void Sans_sequence_rien_n_est_filtre()
    {
        var passes = new P();
        var a = passes.Ouvrir(0);
        passes.Ouvrir(0);

        Assert.False(passes.Depassee(0, a));
        Assert.True(passes.Retenir(0, "x"));
        Assert.True(passes.Retenir(0, "x"));
    }

    [Fact]
    public void Une_seconde_passe_identique_ne_republie_pas_une_differente_si()
    {
        var passes = new P();
        var sansDetails = P.Empreinte(new { Name = "Ketsui", Genre = "" });
        var avecDetails = P.Empreinte(new { Name = "Ketsui", Genre = "Shoot'em up" });

        Assert.True(passes.Retenir(30, sansDetails));
        Assert.False(passes.Retenir(30, P.Empreinte(new { Name = "Ketsui", Genre = "" })));
        Assert.True(passes.Retenir(30, avecDetails));
        // Le meme jeu choisi de nouveau (nouvelle sequence) se publie toujours.
        Assert.True(passes.Retenir(31, avecDetails));
    }
}
