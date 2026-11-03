using System.Text.Json.Nodes;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE PASSEPORT D'UN DEFI SUR ETAT (defaut du 02 au 03/11). Le depart d'un defi nomme un chargement d'etat permis, pose en int
/// dans le brouillon tout juste cree ; le passeport le relisait par « (long?) », qui leve une exception sur un JsonValue&lt;int&gt;.
/// Chaque tentative terminee sur etat (Green Hill 2 en 0:35.25, Metal Slug 3) partait aux brouillons refuses, sans envoi.
/// </summary>
public class DefiChargementsPermisTests
{
    [Fact]
    public void Le_depart_d_un_defi_sur_etat_se_relit_tout_juste_pose()
    {
        var r = Reglement.Lire(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-2.json")));
        var depart = PasseportDeDefi.Depart(r)!;
        Assert.ThrowsAny<InvalidOperationException>(() => (long?)depart["chargements_permis"]);
        Assert.Equal(1, NelfePlayScoringReporter.EntierDuJson(depart["chargements_permis"]));
    }

    [Fact]
    public void Un_entier_se_lit_pose_en_int_en_long_ou_lu_d_un_fichier()
    {
        Assert.Equal(3, NelfePlayScoringReporter.EntierDuJson(JsonValue.Create(3)));
        Assert.Equal(3, NelfePlayScoringReporter.EntierDuJson(JsonValue.Create(3L)));
        Assert.Equal(3, NelfePlayScoringReporter.EntierDuJson(JsonNode.Parse("{\"n\":3}")!["n"]));
        Assert.Null(NelfePlayScoringReporter.EntierDuJson(null));
        Assert.Null(NelfePlayScoringReporter.EntierDuJson(JsonValue.Create("3")));
    }
}
