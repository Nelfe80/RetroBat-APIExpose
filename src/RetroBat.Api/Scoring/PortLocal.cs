namespace RetroBat.Api.Scoring;

/// <summary>
/// LE PORT DE CETTE BORNE, retrouve a ses appuis (1CC MULTI, 2026-09-30).
///
/// En netplay, chaque borne voit les appuis de TOUS les joueurs, chacun sur son port de manette
/// (0 = joueur 1, l'hote ; 1 = joueur 2...). RetroArch sait quel joueur est local mais ne le dit
/// nulle part de lisible ; la plateforme attribue les places, et c'est ici qu'on le verifie. Le
/// wrapper (0.340) dit chaque seconde combien d'appuis chaque port a recus ; le panel, lui, dit
/// ceux de cette borne. Le port dont les appuis SUIVENT ceux du panel, seconde apres seconde, est
/// le sien : les appuis locaux arrivent a l'image meme, ceux des autres n'ont rien a voir avec lui.
///
/// On correle (Pearson) des fenetres de deux secondes, pour absorber le decalage entre la seconde du
/// wrapper et celle ou l'API compte le panel. On ne tranche que sur assez d'appuis et avec un
/// ecart net sur le deuxieme port ; sinon on ne dit rien, et la place de la plateforme fait foi.
/// </summary>
public sealed class PortLocal
{
    /// <summary>Appuis du panel en dessous desquels on ne tranche pas.</summary>
    public const int AppuisMinimum = 30;

    /// <summary>
    /// Correlation minimale (Pearson) du port retenu avec le panel. Des comptes d'appuis sont
    /// toujours positifs : sans les centrer, deux joueurs independants se « ressemblent » deja a
    /// 0,86 (cosinus). Centres, ils tombent pres de zero, et le joueur local reste pres de 1.
    /// </summary>
    public const double RessemblanceMinimale = 0.5;

    /// <summary>Ecart minimal avec le deuxieme port le plus correle.</summary>
    public const double EcartMinimal = 0.3;

    private readonly object _verrou = new();
    private readonly List<int[]> _ports = [];
    private readonly List<int> _panel = [];
    private int _panelEnCours;

    /// <summary>Un appui du panel de cette borne.</summary>
    public void AppuiDuPanel()
    {
        lock (_verrou) { _panelEnCours++; }
    }

    /// <summary>Une seconde du wrapper : les appuis de chaque port. Ferme la seconde du panel.</summary>
    public void SecondeDuWrapper(IReadOnlyList<int> appuis)
    {
        lock (_verrou)
        {
            _ports.Add(appuis.ToArray());
            _panel.Add(_panelEnCours);
            _panelEnCours = 0;
        }
    }

    public void Vider()
    {
        lock (_verrou)
        {
            _ports.Clear();
            _panel.Clear();
            _panelEnCours = 0;
        }
    }

    /// <summary>Le port de cette borne d'apres ce qui a ete vu, ou null.</summary>
    public int? Estimer()
    {
        lock (_verrou) { return Estimer(_ports, _panel); }
    }

    /// <summary>Le port dont les appuis suivent le mieux ceux du panel, ou null si l'on ne peut pas trancher.</summary>
    public static int? Estimer(IReadOnlyList<int[]> ports, IReadOnlyList<int> panel)
    {
        var n = Math.Min(ports.Count, panel.Count);
        if (n < 2 || panel.Take(n).Sum() < AppuisMinimum) return null;
        var nbPorts = ports.Take(n).Max(p => p.Length);

        // Fenetres glissantes de deux secondes.
        var panneau = Fenetres(Enumerable.Range(0, n).Select(i => (double)panel[i]).ToList());
        var ressemblances = new List<(int Port, double Correlation)>();
        for (var port = 0; port < nbPorts; port++)
        {
            var serie = Fenetres(Enumerable.Range(0, n).Select(i => port < ports[i].Length ? (double)ports[i][port] : 0).ToList());
            ressemblances.Add((port, Correlation(panneau, serie)));
        }
        var tries = ressemblances.OrderByDescending(r => r.Correlation).ToList();
        var meilleur = tries[0];
        var second = tries.Count > 1 ? tries[1].Correlation : 0;
        return meilleur.Correlation >= RessemblanceMinimale && meilleur.Correlation - second >= EcartMinimal
            ? meilleur.Port
            : null;
    }

    private static List<double> Fenetres(List<double> serie)
        => Enumerable.Range(0, Math.Max(0, serie.Count - 1)).Select(i => serie[i] + serie[i + 1]).ToList();

    /// <summary>Correlation de Pearson ; 0 pour une serie constante (un port jamais touche).</summary>
    private static double Correlation(List<double> a, List<double> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n == 0) return 0;
        double ma = 0, mb = 0;
        for (var i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n;
        mb /= n;
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < n; i++)
        {
            ab += (a[i] - ma) * (b[i] - mb);
            aa += (a[i] - ma) * (a[i] - ma);
            bb += (b[i] - mb) * (b[i] - mb);
        }
        return aa == 0 || bb == 0 ? 0 : ab / Math.Sqrt(aa * bb);
    }
}
