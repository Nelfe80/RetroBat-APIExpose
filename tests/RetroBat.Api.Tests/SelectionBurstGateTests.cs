using RetroBat.Providers.EmulationStation;
using Xunit;
using Xunit.Abstractions;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, PERF-API-08 : l'attente d'accalmie de la porte anti-rafale suit l'espacement de la rafale.
/// La simulation rejoue la porte reelle sur une horloge fictive : delai entre la derniere selection et sa publication,
/// et nombre de publications, avant (attente fixe de 600 ms) et apres (300 a 600 ms).
/// </summary>
public class SelectionBurstGateTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Progress = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SettleAvant = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SettleApres = TimeSpan.FromMilliseconds(300);

    private readonly ITestOutputHelper _sortie;

    public SelectionBurstGateTests(ITestOutputHelper sortie) => _sortie = sortie;

    /// <summary>Rejoue des arrivees (ms) ; rend le delai entre la derniere arrivee et sa publication, et le nombre de publications.</summary>
    private static (double DelaiFinalMs, int Publications) Rejouer(IReadOnlyList<double> arriveesMs, TimeSpan settleMin)
    {
        var gate = new SelectionBurstGate();
        var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var publications = 0;
        double? echeance = null;   // minuterie d'accalmie armee
        double derniereArrivee = 0, derniereSortie = -1;

        for (var i = 0; i < arriveesMs.Count; i++)
        {
            var t = arriveesMs[i];
            if (echeance is { } e && e <= t)
            {
                publications++;
                gate.OnPublished(t0.AddMilliseconds(e));
                derniereSortie = e;
                echeance = null;
            }

            var verdict = gate.OnArrival(t0.AddMilliseconds(t), Quiet, Progress, settleMin, deferLeadingEdge: false);
            derniereArrivee = t;
            if (verdict.Decision == SelectionBurstGate.Decision.Park)
            {
                echeance = t + verdict.Settle.TotalMilliseconds;
            }
            else
            {
                publications++;
                echeance = null;
                derniereSortie = t;
            }
        }

        if (echeance is { } fin)
        {
            publications++;
            derniereSortie = fin;
        }

        return (derniereSortie - derniereArrivee, publications);
    }

    private static List<double> Rafale(int nombre, double ecartMs) => Enumerable.Range(0, nombre).Select(i => 1000 + i * ecartMs).ToList();

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void Defilement_rapide_s_affiche_en_300_ms_au_lieu_de_600(double ecartMs)
    {
        var avant = Rejouer(Rafale(15, ecartMs), SettleAvant);
        var apres = Rejouer(Rafale(15, ecartMs), SettleApres);
        _sortie.WriteLine($"ecart {ecartMs} ms : avant {avant.DelaiFinalMs} ms ({avant.Publications} publications), apres {apres.DelaiFinalMs} ms ({apres.Publications} publications)");

        Assert.Equal(600, avant.DelaiFinalMs);
        Assert.Equal(Math.Max(300, 1.5 * ecartMs), apres.DelaiFinalMs);
        Assert.True(apres.Publications <= avant.Publications + 1);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public void File_d_ES_lente_garde_600_ms_et_n_est_pas_rejouee(double ecartMs)
    {
        var avant = Rejouer(Rafale(10, ecartMs), SettleAvant);
        var apres = Rejouer(Rafale(10, ecartMs), SettleApres);
        _sortie.WriteLine($"ecart {ecartMs} ms : avant {avant.DelaiFinalMs} ms ({avant.Publications} publications), apres {apres.DelaiFinalMs} ms ({apres.Publications} publications)");

        Assert.Equal(avant.DelaiFinalMs, apres.DelaiFinalMs);
        Assert.Equal(avant.Publications, apres.Publications);
    }

    [Fact]
    public void Premier_jeu_apres_un_silence_part_tout_de_suite()
    {
        var gate = new SelectionBurstGate();
        var t = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(SelectionBurstGate.Decision.Immediate, gate.OnArrival(t, Quiet, Progress, SettleApres, false).Decision);
        Assert.Equal(SelectionBurstGate.Decision.Park, gate.OnArrival(t.AddMilliseconds(100), Quiet, Progress, SettleApres, false).Decision);
        Assert.Equal(SelectionBurstGate.Decision.Immediate, gate.OnArrival(t.AddMilliseconds(800), Quiet, Progress, SettleApres, false).Decision);
    }

    [Fact]
    public void Apres_un_rechargement_d_ES_l_attente_reste_entiere()
    {
        var gate = new SelectionBurstGate();
        var t = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var v1 = gate.OnArrival(t, Quiet, Progress, SettleApres, deferLeadingEdge: true);
        var v2 = gate.OnArrival(t.AddMilliseconds(80), Quiet, Progress, SettleApres, deferLeadingEdge: true);
        Assert.Equal(SelectionBurstGate.Decision.Park, v1.Decision);
        Assert.Equal(Quiet, v1.Settle);
        Assert.Equal(Quiet, v2.Settle);
    }

    [Fact]
    public void Longue_rafale_echantillonnee_toutes_les_800_ms()
    {
        var gate = new SelectionBurstGate();
        var t = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var echantillons = 0;
        for (var i = 0; i < 40; i++)
        {
            if (gate.OnArrival(t.AddMilliseconds(i * 100), Quiet, Progress, SettleApres, false).Decision == SelectionBurstGate.Decision.Sample)
                echantillons++;
        }

        Assert.Equal(4, echantillons);   // 800, 1600, 2400, 3200 ms
    }
}
