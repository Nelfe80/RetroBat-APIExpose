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
    /// <summary>Releve d'audience : ce qui est joue et combien de temps, jamais par qui.</summary>
    public bool PlayReportingEnabled { get; set; } = true;

    /// <summary>Decouverte silencieuse du scoring : voir <see cref="ScoringDiscoveryOptions"/>.</summary>
    public ScoringDiscoveryOptions ScoringDiscovery { get; set; } = new();
}
