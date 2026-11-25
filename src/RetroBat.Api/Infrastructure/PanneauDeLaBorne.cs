namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Instantane des appareils vus par SDL au dernier balayage (pose par <see cref="PanelInputWatcherService"/>) : de quoi savoir si
/// l'API sait voir les appuis du joueur (NelfePlayScoringReporter.DepartVu).
/// </summary>
public static class PanneauDeLaBorne
{
    /// <summary>Null tant que SDL n'a rien enumere.</summary>
    public static volatile IReadOnlyList<PlayerNumbering.Device>? Appareils;
}
