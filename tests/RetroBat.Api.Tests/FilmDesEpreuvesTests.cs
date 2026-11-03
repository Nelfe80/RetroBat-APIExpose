using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE REPLAY ET LE FILM DES EPREUVES (03/11). Le film d'une epreuve ne garde que la tentative retenue (la seance enchaine
/// essais ratés et relances). Le lien replay d'un speedrun etait note pose alors que le site repondait « submission.unknown »
/// avec un code 200 : il ne repartait jamais.
/// </summary>
public class FilmDesEpreuvesTests
{
    [Fact]
    public void Le_film_d_une_epreuve_va_de_peu_avant_le_depart_a_peu_apres_l_arrivee()
    {
        // Enregistrement parti a la frame 1000 ; tentative de 5000 a 6695 (1-1 en 0:28.20).
        var bornes = BornesVideo.DuDefi(1000, 9000, 5000, 6695);
        Assert.Equal((5000 - BornesVideo.MargeDeDebut - 1000, 6695 + BornesVideo.MargeDeFin - 1000), bornes);
    }

    [Fact]
    public void Les_bornes_restent_dans_l_enregistrement()
    {
        Assert.Equal((0L, 6800L - 1000), BornesVideo.DuDefi(1000, 6800, 1050, 6695));
        Assert.Null(BornesVideo.DuDefi(1000, 4000, 5000, 6695));
        Assert.Null(BornesVideo.DuDefi(7000, null, 5000, 6695));
        Assert.Null(BornesVideo.DuDefi(1000, null, 5000, 5000));
    }

    [Theory]
    [InlineData(200, "{\"ok\":true,\"status\":\"linked\",\"submission_id\":\"x\"}", "Pose")]
    [InlineData(200, "{\"ok\":false,\"status\":\"error\",\"reason\":\"submission.unknown\"}", "ARetenter")]
    [InlineData(200, "{\"ok\":false,\"status\":\"error\",\"reason\":\"submission.not_owned\"}", "Refuse")]
    [InlineData(200, "<html>relais</html>", "ARetenter")]
    [InlineData(503, "", "ARetenter")]
    [InlineData(429, "", "ARetenter")]
    [InlineData(404, "", "Refuse")]
    public void Seul_linked_pose_le_lien_et_une_partie_inconnue_se_retente(int statut, string corps, string attendu)
        => Assert.Equal(attendu, NelfePlayScoringReporter.IssueDuLienReplay(statut, corps).ToString());
}
