namespace RetroBat.Providers.EmulationStation;

/// <summary>
/// La decision de la porte anti-rafale des game-selected, sans horloge ni minuterie : le watcher lui donne l'instant
/// d'arrivee, elle dit s'il faut publier tout de suite, publier un echantillon, ou garer la selection et attendre
/// une accalmie (et combien de temps).
///
/// (brief du 13/11, PERF-API-08) L'attente d'accalmie n'est plus toujours <c>SelectionBurstQuietMs</c> (600 ms) :
/// elle suit l'espacement observe dans la rafale, une fois et demie le plus grand des trois derniers ecarts, entre
/// <c>SelectionBurstSettleMinMs</c> (300 ms) et 600 ms. Un defilement rapide (ecarts de 50 a 200 ms) s'affiche
/// donc 300 ms apres la derniere selection au lieu de 600 ; une file de scripts d'ES qui se vide lentement (ecarts
/// de 400 ms et plus) garde l'attente de 600 ms, et n'est pas rejouee. Le seuil du « premier jeu apres un silence »
/// (publie sans attendre) reste <c>SelectionBurstQuietMs</c>.
/// </summary>
public sealed class SelectionBurstGate
{
    public enum Decision
    {
        /// <summary>Premier jeu apres un silence : publier tout de suite.</summary>
        Immediate,
        /// <summary>Longue rafale : echantillon periodique, publier tout de suite.</summary>
        Sample,
        /// <summary>Garer la selection et la publier apres <see cref="Verdict.Settle"/> de silence.</summary>
        Park,
    }

    public readonly record struct Verdict(Decision Decision, TimeSpan Settle);

    private const int EcartsRetenus = 3;
    private const double MargeSurLEcart = 1.5;

    private readonly Queue<TimeSpan> _ecarts = new();
    private DateTime _derniereArrivee = DateTime.MinValue;
    private DateTime _dernierePublication = DateTime.MinValue;

    /// <summary>
    /// Une selection arrive a <paramref name="now"/>. <paramref name="quiet"/> : silence qui fait d'une selection
    /// un choix delibere (et attente maximale) ; <paramref name="progress"/> : echantillonnage d'une longue rafale ;
    /// <paramref name="settleMin"/> : attente minimale ; <paramref name="deferLeadingEdge"/> : juste apres un
    /// rechargement d'ES, rien ne part tout de suite et l'attente reste <paramref name="quiet"/>.
    /// </summary>
    public Verdict OnArrival(DateTime now, TimeSpan quiet, TimeSpan progress, TimeSpan settleMin, bool deferLeadingEdge)
    {
        var ecart = now - _derniereArrivee;
        _derniereArrivee = now;

        if (ecart >= quiet)
        {
            _ecarts.Clear();
            if (!deferLeadingEdge)
            {
                _dernierePublication = now;
                return new Verdict(Decision.Immediate, TimeSpan.Zero);
            }
        }
        else
        {
            _ecarts.Enqueue(ecart);
            while (_ecarts.Count > EcartsRetenus) _ecarts.Dequeue();
        }

        if (!deferLeadingEdge && now - _dernierePublication >= progress)
        {
            _dernierePublication = now;
            return new Verdict(Decision.Sample, TimeSpan.Zero);
        }

        return new Verdict(Decision.Park, deferLeadingEdge ? quiet : Accalmie(quiet, settleMin));
    }

    /// <summary>La selection garee vient d'etre publiee a l'accalmie.</summary>
    public void OnPublished(DateTime now) => _dernierePublication = now;

    private TimeSpan Accalmie(TimeSpan quiet, TimeSpan settleMin)
    {
        if (settleMin >= quiet || _ecarts.Count == 0) return quiet;
        var plusGrand = _ecarts.Max();
        var attente = TimeSpan.FromTicks((long)(plusGrand.Ticks * MargeSurLEcart));
        if (attente < settleMin) return settleMin;
        return attente > quiet ? quiet : attente;
    }
}
