using RetroBat.Api.MassRun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Brief du 13/11, MASS-03 : au-dela de 64 coureurs, ceux qui sont pres du joueur, pas les premiers inscrits.</summary>
public class MassRunCoureursProchesTests
{
    [Fact]
    public void Salle_de_120_le_joueur_au_milieu_voit_ses_64_voisins()
    {
        // 120 coureurs inscrits dans l'ordre, alignes de 10 en 10 pixels ; le joueur est a x = 900 (le 90e).
        var coureurs = Enumerable.Range(0, 120).Select(i => ((ushort)(i + 1), 0x010101, i * 10)).ToList();
        var retenus = ClientDeSalle.LesPlusProches(coureurs, 0x010101, 900, MassRunCanal.MaxCoureurs);

        Assert.Equal(64, retenus.Count);
        Assert.Contains((ushort)91, retenus);       // celui qui est sur le joueur
        Assert.Contains((ushort)120, retenus);      // le dernier inscrit, juste devant
        Assert.DoesNotContain((ushort)1, retenus);  // le premier inscrit, loin derriere
        var ecartMax = coureurs.Where(c => retenus.Contains(c.Item1)).Max(c => Math.Abs(c.Item3 - 900));
        var ecartMinExclu = coureurs.Where(c => !retenus.Contains(c.Item1)).Min(c => Math.Abs(c.Item3 - 900));
        Assert.True(ecartMax <= ecartMinExclu);
    }

    [Fact]
    public void La_meme_scene_passe_avant_une_autre_scene_plus_proche_en_x()
    {
        var coureurs = new List<(ushort, int, int)>
        {
            (1, 0x020101, 1000),   // sous-sol (autre scene), meme x que le joueur
            (2, 0x010101, 1300),
            (3, 0x010101, 700),
        };
        var retenus = ClientDeSalle.LesPlusProches(coureurs, 0x010101, 1000, 2);
        Assert.Equal(new HashSet<ushort> { 2, 3 }, retenus);
    }

    [Fact]
    public void Ecarts_egaux_departages_par_la_place()
    {
        var coureurs = new List<(ushort, int, int)> { (5, 1, 110), (4, 1, 90), (9, 1, 100) };
        Assert.Equal(new HashSet<ushort> { 9, 4 }, ClientDeSalle.LesPlusProches(coureurs, 1, 100, 2));
    }
}
