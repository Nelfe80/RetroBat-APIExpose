using RetroBat.Api.Scoring.Discovery;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Ce que cette borne remonte a NelfePlay.
///
/// Les interrupteurs d'ici sont reels : une mesure qu'on ne peut pas eteindre n'est pas une
/// mesure. <see cref="PlayReportingEnabled"/> existait deja, lu directement par Program.cs ;
/// il est repris ici pour que les deux releves se reglent au meme endroit.
/// </summary>
public sealed class NelfePlayOptions
{
    /// <summary>
    /// Les surfaces NelfePlay de cette borne. A false, la collection « World Scoring » n'est
    /// plus synchronisee ni affichee. Les consentements de releve ci-dessous restent
    /// independants : afficher une collection n'allume aucune remontee, et l'eteindre ne
    /// rallume rien.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Afficher dans EmulationStation la collection des jeux ouverts au scoring mondial,
    /// limitee a ceux que la borne possede et peut reellement mesurer.
    /// </summary>
    public bool ShowScoringCollection { get; set; } = true;

    /// <summary>Releve d'audience : ce qui est joue et combien de temps, jamais par qui.</summary>
    public bool PlayReportingEnabled { get; set; } = true;

    /// <summary>
    /// CE QUI A LE DROIT DE PARAITRE PAR-DESSUS UN JEU EN COURS.
    ///
    /// Ce reglage ne concerne QUE l'affichage en jeu : il ne coupe ni les notifications au menu
    /// d'EmulationStation, ni le push, ni la liste du compte sur le site. Une famille sous le
    /// seuil arrive au menu, pas par-dessus la partie.
    ///
    /// Le defaut laisse passer ce qui concerne le score du joueur assis, et rien d'autre. Une
    /// borne d'exposition ou un stream se regle sur `Off`.
    /// </summary>
    public InGameMessageLevel InGameMessageLevel { get; set; } = InGameMessageLevel.Score;

    /// <summary>Decouverte silencieuse du scoring : voir <see cref="ScoringDiscoveryOptions"/>.</summary>
    public ScoringDiscoveryOptions ScoringDiscovery { get; set; } = new();

    /// <summary>
    /// Forcer la certification : avant un jeu ouvert au scoring, la borne applique les reglages
    /// certifies publies par son profil (difficulte, vies, vitesse, cheats), au lieu de laisser le
    /// joueur decouvrir a la fin de la partie que son score est refuse. Rien qui touche l'affichage
    /// ni les manettes. A false, la borne se contente d'informer.
    /// </summary>
    public bool ForceCertifiedSettings { get; set; } = true;
}
