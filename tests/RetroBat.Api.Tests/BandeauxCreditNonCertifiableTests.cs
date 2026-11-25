using RetroBat.Api.Infrastructure;
using Xunit;
using B = RetroBat.Api.Infrastructure.NelfePlayScoringReporter.BandeauDeCredit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Decision du user (25/11) : quand le prevol dit NON CERTIFIABLE (option de triche du coeur, 1.9.215, ou tout autre motif),
/// les bandeaux de credit ne disent plus « certifie » (continue, nouvelle partie, joueur qui rejoint).
/// </summary>
public class BandeauxCreditNonCertifiableTests
{
    [Theory]
    [InlineData("ScoreCertifie")]
    [InlineData("NouvellePartie")]
    [InlineData("JoueurRejoint")]
    [InlineData("NonCertifiable")]
    [InlineData("PartieAPlusieurs")]
    [InlineData("Aucun")]
    public void Partie_certifiable_garde_tous_ses_bandeaux(string nom)
    {
        var bandeau = Enum.Parse<B>(nom);
        Assert.True(NelfePlayScoringReporter.BandeauAutorise(bandeau, prevolRefuse: false));
    }

    [Theory]
    [InlineData("ScoreCertifie")]
    [InlineData("NouvellePartie")]
    [InlineData("JoueurRejoint")]
    public void Triche_active_aucun_bandeau_ne_dit_certifie(string nom)
    {
        var bandeau = Enum.Parse<B>(nom);
        Assert.False(NelfePlayScoringReporter.BandeauAutorise(bandeau, prevolRefuse: true));
    }

    [Theory]
    [InlineData("NonCertifiable")]
    [InlineData("PartieAPlusieurs")]
    [InlineData("Aucun")]
    public void Triche_active_les_bandeaux_d_information_parlent_toujours(string nom)
    {
        var bandeau = Enum.Parse<B>(nom);
        Assert.True(NelfePlayScoringReporter.BandeauAutorise(bandeau, prevolRefuse: true));
    }

    [Fact]
    public void Nouvelle_partie_apres_game_over_sous_triche_est_tue_mais_reste_une_nouvelle_partie()
    {
        var b = NelfePlayScoringReporter.QuelBandeau(depart: false, arrivee: false, premiereFin: false, dejaADeux: false, scoreSolo: true, nouvellePartie: true);
        Assert.Equal(B.NouvellePartie, b);
        Assert.False(NelfePlayScoringReporter.BandeauAutorise(b, prevolRefuse: true));
        Assert.True(NelfePlayScoringReporter.BandeauAutorise(b, prevolRefuse: false));
    }
}
