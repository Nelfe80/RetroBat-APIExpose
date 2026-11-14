namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Ce qui reste a annoncer des directs suivis : un direct nouveau, jamais deux fois. La premiere liste est retenue sans
/// bruit (un redemarrage de l'API ne reannonce pas ce que le joueur a deja vu) ; un direct fini pourra etre reannonce s'il
/// reprend. Sert au guet de 60 s comme au releve de l'agent (point 5 du 14/11). Sans reseau ni horloge.
/// </summary>
public sealed class GuetDesDirectsSuivis
{
    private readonly HashSet<string> _connus = new(StringComparer.Ordinal);
    private bool _premiereListe = true;
    private readonly object _verrou = new();

    /// <summary>Les directs de <paramref name="directs"/> a annoncer maintenant.</summary>
    public IReadOnlyList<LeaderboardSocialClient.Direct> Nouveaux(IReadOnlyList<LeaderboardSocialClient.Direct> directs)
    {
        lock (_verrou)
        {
            var nouveaux = new List<LeaderboardSocialClient.Direct>();
            foreach (var d in directs)
            {
                if (_connus.Add(d.Session) && !_premiereListe) nouveaux.Add(d);
            }

            _connus.IntersectWith(directs.Select(d => d.Session));
            _premiereListe = false;
            return nouveaux;
        }
    }
}
