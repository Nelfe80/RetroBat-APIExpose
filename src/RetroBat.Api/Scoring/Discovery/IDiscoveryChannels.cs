namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Le côté émulateur : on lui demande de copier des images, et rien d'autre.
///
/// Deux implémentations derrière cette interface, un tuyau nommé pour le wrapper libretro et
/// un socket pour le plugin Lua de MAME, mais le coordinateur n'a pas à savoir laquelle est
/// branchée. Aucune méthode ne rend d'image : les pixels ne passent jamais par APIExpose.
/// </summary>
public interface IEmulatorCaptureChannel
{
    /// <summary>Le composant de capture répond pour l'émulateur en cours.</summary>
    bool IsAvailable { get; }

    /// <summary>Demande une rafale. Rend faux si l'émulateur n'a pas pu s'armer.</summary>
    Task<bool> ArmAsync(string token, int frames, CancellationToken cancellationToken);

    /// <summary>Coupe la capture, sans attendre la fin de la rafale.</summary>
    Task DisarmAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Le côté vérificateur : on lui dit ce que la mémoire annonce, il rend des nombres.
/// </summary>
public interface IScoreVerifierChannel
{
    /// <summary>Le processus répond.</summary>
    bool IsAvailable { get; }

    /// <summary>Ce qu'il faut chercher, jusqu'à nouvel ordre.</summary>
    Task<bool> ExpectAsync(string token, long value, FrameOrientationDegrees orientation, CancellationToken cancellationToken);

    /// <summary>Entre deux jeux : la police apprise pour l'un ne vaut rien pour l'autre.</summary>
    Task ForgetAsync(CancellationToken cancellationToken);

    /// <summary>Un résultat est arrivé, pour le jeton et la valeur qui l'ont demandé.</summary>
    event Action<string, VerificationOutcome>? ResultReceived;
}

/// <summary>
/// L'orientation telle que le référentiel arcade la donne, en degrés : le vérificateur
/// tourne l'image, l'émulateur ne tourne rien.
/// </summary>
public enum FrameOrientationDegrees
{
    Upright = 0,
    Clockwise90 = 90,
    UpsideDown = 180,
    CounterClockwise90 = 270
}
