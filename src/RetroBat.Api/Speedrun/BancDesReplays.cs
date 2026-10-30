using System.Globalization;

namespace RetroBat.Api.Speedrun;

/// <summary>Un passage mesure dans un replay : une tentative partie, et ce qu'elle est devenue.</summary>
public sealed record PassageMesure(
    string Defi,
    long Depart,
    ResultatTentative Resultat,
    int PointsDeControle,
    int EcartsLus)
{
    /// <summary>
    /// fidele : un point de controle au moins dans le passage, et aucun ecart sur une adresse du .MEM ;
    /// divergent : un ecart sur une adresse lue ; non_verifie : aucun point de controle dans le passage.
    /// </summary>
    public string Fidelite => EcartsLus > 0 ? "divergent" : PointsDeControle > 0 ? "fidele" : "non_verifie";
}

/// <summary>La trace d'un replay rejoue par la sonde : valeurs lues image par image et points de controle.</summary>
public sealed class TraceDeReplay
{
    public sealed record Lecture(long Frame, long Adresse, string Type, long Valeur);

    public List<Lecture> Lectures { get; } = new();

    /// <summary>Les points de controle : image et nombre d'octets lus par le .MEM qui differaient.</summary>
    public List<(long Frame, int Lus)> Points { get; } = new();

    public long Images { get; private set; }

    public double? CadenceDuCoeur { get; private set; }

    public static TraceDeReplay Lire(IEnumerable<string> lignes)
    {
        var t = new TraceDeReplay();
        foreach (var brute in lignes)
        {
            var l = brute.Trim();
            if (l.Length == 0) continue;
            var champs = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (l[0] != '#')
            {
                if (champs.Length < 4) continue;
                t.Lectures.Add(new Lecture(long.Parse(champs[0], CultureInfo.InvariantCulture), Hex.Lire(champs[1]), champs[2],
                    long.Parse(champs[3], CultureInfo.InvariantCulture)));
                continue;
            }

            // « # point image N octets B lus L ... »
            if (champs.Length >= 8 && champs[1] == "point" && champs[2] == "image" && champs[6] == "lus")
                t.Points.Add((long.Parse(champs[3], CultureInfo.InvariantCulture), int.Parse(champs[7], CultureInfo.InvariantCulture)));
            else if (champs.Length >= 3 && champs[1] == "images")
                t.Images = long.Parse(champs[2], CultureInfo.InvariantCulture);
            else if (champs.Length >= 3 && champs[1] == "fps_du_coeur")
                t.CadenceDuCoeur = double.Parse(champs[2], CultureInfo.InvariantCulture);
        }

        return t;
    }
}

/// <summary>
/// LE BANC DES SPEEDRUNS SUR LES REPLAYS (lot S2). Les evenements que le wrapper aurait emis passent par un
/// evaluateur par reglement ; une tentative close, une autre attend le depart suivant. Rien n'est envoye :
/// le resultat est un rapport. Les valeurs a relever directement (READ_CORE_RAM sur la borne) sont prises
/// dans la trace, a l'image ou l'evaluateur les demande.
/// </summary>
public static class BancDesReplays
{
    public static List<PassageMesure> Mesurer(TraceDeReplay trace, LignesDuMem lignes, IReadOnlyList<(string Nom, Reglement Reglement)> defis)
    {
        var sim = new WrapperSimule(lignes);
        var passages = new List<PassageMesure>();
        var courants = defis.Select(d => new Evaluateur(d.Reglement)).ToArray();
        var coupuresVues = 0;

        void Relever(Evaluateur ev)
        {
            foreach (var a in ev.AdressesInconnues)
                if (sim.Valeur(a) is { } v) ev.Relever(a, v);
        }

        void Clore(int i, Evaluateur ev)
        {
            var r = ev.Resultat();
            var depart = r.Journal.FirstOrDefault(c => c.Role == "start");
            if (depart is not null)
            {
                var fin = r.FrameDeFin ?? trace.Images;
                var points = trace.Points.Where(p => p.Frame >= depart.Frame && p.Frame <= fin).ToList();
                passages.Add(new PassageMesure(defis[i].Nom, depart.Frame, r, points.Count, points.Count(p => p.Lus > 0)));
            }

            courants[i] = new Evaluateur(defis[i].Reglement);
            Relever(courants[i]);
        }

        // Une image a la fois : le wrapper emet les evenements d'une image ensemble.
        foreach (var image in trace.Lectures.GroupBy(l => l.Frame))
        {
            var signaux = image.SelectMany(l => sim.Lire(l.Frame, l.Adresse, l.Type, l.Valeur)).ToList();
            if (signaux.Count > 0)
            {
                for (var i = 0; i < courants.Length; i++)
                {
                    var ev = courants[i];
                    Relever(ev);
                    ev.RecevoirImage(signaux);
                    Relever(ev);
                    if (ev.EstTerminee) Clore(i, ev);
                }
            }

            for (; coupuresVues < sim.Coupures.Count; coupuresVues++)
            {
                for (var i = 0; i < courants.Length; i++)
                {
                    courants[i].CapteurCoupe(sim.Coupures[coupuresVues].Adresse);
                    if (courants[i].EstTerminee) Clore(i, courants[i]);
                }
            }
        }

        // Le replay s'arrete : une tentative en cours est close comme le jeu qu'on ferme.
        for (var i = 0; i < courants.Length; i++)
        {
            courants[i].Fermer();
            Clore(i, courants[i]);
        }

        return passages.OrderBy(p => p.Depart).ThenBy(p => p.Defi, StringComparer.Ordinal).ToList();
    }
}
