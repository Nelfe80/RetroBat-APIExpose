namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// L'état du reporter pour une partie, décidé une fois au lancement (CDC §9.1).
/// </summary>
public enum ScoringMode
{
    /// <summary>Rien ne se mesure, rien ne s'arme, rien ne part.</summary>
    Disabled,

    /// <summary>Le jeu n'a pas de profil : la borne observe, sans rien afficher.</summary>
    SilentDiscovery,

    /// <summary>Le jeu a un profil ouvert et compatible : protocole officiel.</summary>
    OfficialScoring,

    /// <summary>Empreinte invalidée : accusé technique minimal, aucune découverte.</summary>
    Quarantined
}

/// <summary>Ce que la plateforme dit de cette borne pour la découverte.</summary>
public enum DiscoveryEnrollment
{
    /// <summary>La plateforme n'en veut pas : rien ne s'arme.</summary>
    Off,

    /// <summary>Elle ingère et mesure, mais n'ouvre aucun profil. C'est le départ.</summary>
    Shadow,

    /// <summary>Elle ingère et peut ouvrir.</summary>
    On
}

/// <summary>Le mode retenu, et la raison, pour que le journal se lise sans relire le code.</summary>
public readonly record struct ScoringModeDecision(ScoringMode Mode, string Reason)
{
    public bool CapturesFrames => Mode == ScoringMode.SilentDiscovery;

    public override string ToString() => $"{Mode} ({Reason})";
}
