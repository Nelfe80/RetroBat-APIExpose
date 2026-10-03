using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'arret du replay a la baisse du score (2026-10-03) : un total de passage, ecrit chiffre par
/// chiffre dans la meme image, ne coupe plus le replay ; une vraie remise a zero le coupe, une fois
/// confirmee par la lecture suivante.
/// </summary>
public sealed class BaisseDuScoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 0, 15, 40, DateTimeKind.Utc);

    /// <summary>Lit une suite de (millisecondes, total) pendant un enregistrement ou le score a monte.</summary>
    private static List<long> Arrets(params (int Ms, long Total)[] lectures)
    {
        var salves = new SalvesDeScore();
        var baisse = new ArretALaBaisse();
        long? precedent = null;
        var arrets = new List<long>();
        foreach (var (ms, total) in lectures)
        {
            var (reference, nouvelle) = salves.Lire(precedent, T0.AddMilliseconds(ms));
            if (baisse.Lire(total, reference, nouvelle, enregistrement: true, monteeVue: true)) arrets.Add(total);
            precedent = total;
        }
        return arrets;
    }

    [Fact]
    public void La_retenue_de_1942_ne_coupe_plus_le_replay()
    {
        // 90 -> 100 : la centaine est ecrite avant la dizaine, l'agregateur publie 190 puis 100.
        Assert.Empty(Arrets((0, 50), (1000, 90), (2000, 190), (2003, 100), (4000, 150), (6000, 250)));
    }

    [Fact]
    public void Une_remise_a_zero_coupe_le_replay_aux_points_suivants()
    {
        Assert.Equal(new long[] { 100 }, Arrets((0, 12000), (1000, 25000), (5000, 0), (30000, 100), (31000, 200)));
    }

    [Fact]
    public void Un_chiffre_ecrit_une_image_avant_l_autre_est_rattrape()
    {
        // 99 -> 100 en deux images : 90 passe, puis 100 a la salve suivante, au-dessus de 99.
        Assert.Empty(Arrets((0, 99), (1000, 90), (1017, 100), (2000, 110)));
    }

    [Fact]
    public void Sans_enregistrement_aucune_baisse_n_attend()
    {
        var baisse = new ArretALaBaisse();
        Assert.False(baisse.Lire(0, 25000, nouvelleSalve: true, enregistrement: false, monteeVue: true));
        Assert.False(baisse.EnAttente);
    }

    [Fact]
    public void Une_baisse_ne_coupe_rien_si_l_enregistrement_s_est_arrete_entre_temps()
    {
        var baisse = new ArretALaBaisse();
        Assert.False(baisse.Lire(0, 25000, nouvelleSalve: true, enregistrement: true, monteeVue: true));
        Assert.True(baisse.EnAttente);
        Assert.False(baisse.Lire(100, 0, nouvelleSalve: true, enregistrement: false, monteeVue: false));
        Assert.False(baisse.EnAttente);
    }

    [Fact]
    public void Les_totaux_d_une_salve_se_comparent_au_score_d_avant_la_salve()
    {
        var salves = new SalvesDeScore();
        Assert.Equal(((long?)null, true), salves.Lire(null, T0));
        Assert.Equal(((long?)90, true), salves.Lire(90, T0.AddSeconds(1)));
        Assert.Equal(((long?)90, false), salves.Lire(190, T0.AddSeconds(1).AddMilliseconds(3)));
        Assert.Equal(((long?)100, true), salves.Lire(100, T0.AddSeconds(2)));
    }
}
