namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Ce que le vérificateur renvoie pour une image : des nombres, et rien d'autre.
/// La forme est celle de <c>VisualMatchResult</c>, recopiée ici pour qu'APIExpose n'ait
/// aucune dépendance vers le binaire du vérificateur.
/// </summary>
public readonly record struct VerificationOutcome(
    bool Matched,
    double Confidence,
    double RegionStability,
    int CandidateCount,
    string VerifierVersion);

/// <summary>Ce qu'une partie a établi, tel qu'il entrera dans l'enveloppe.</summary>
public sealed record DiscoverySessionEvidence(
    int Attempts,
    int Matches,
    int DistinctValuesMatched,
    double MeanConfidence,
    double MinConfidence,
    double MeanRegionStability,
    int MaxCandidateCount,
    bool LikelyNotShownAsDigits,
    string VerifierVersion,
    bool IsCredible,
    string Verdict);

/// <summary>
/// Le suivi temporel du §7.2.6 : il ne suffit pas qu'un nombre ait été reconnu une fois.
///
/// Une reconnaissance isolée ne prouve rien. Un « 0 » se lit partout sur un écran d'arcade, et
/// même un nombre plus long peut coïncider une fois avec un compteur de temps, de munitions ou
/// de niveau. Ce qui prouve, c'est que PLUSIEURS valeurs différentes, annoncées par la mémoire
/// à des moments différents, se sont lues au MÊME endroit de l'image. Cela n'arrive que si cet
/// endroit est le champ de score.
///
/// D'où les trois exigences, cumulatives : assez de valeurs distinctes, une confiance qui tient
/// le seuil, et une région stable. Deux valeurs ne suffisent pas, et ce n'est pas de la
/// prudence de principe : un score qui passe de 0 à 100 pendant qu'un compteur de vies passe
/// de 3 à 2 laisse encore trop de coïncidences possibles.
///
/// La classe ne garde aucune image, aucune région, aucun pixel : des compteurs et des
/// moyennes. C'est ce qui permet au schéma d'enveloppe de rester vrai sans effort.
/// </summary>
public sealed class DiscoveryTracker
{
    private readonly ScoringDiscoveryOptions _options;
    private readonly HashSet<long> _distinctMatched = new();

    private int _attempts;
    private int _matches;
    private double _confidenceSum;
    private double _minConfidence = double.MaxValue;
    private double _stabilitySum;
    private int _stabilitySamples;
    private int _maxCandidates;
    private int _consecutiveWithoutCandidate;
    private string _verifierVersion = string.Empty;

    public DiscoveryTracker(ScoringDiscoveryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public int Attempts => _attempts;

    public int Matches => _matches;

    public int DistinctValuesMatched => _distinctMatched.Count;

    /// <summary>
    /// Assez de tentatives d'affilee sans le moindre candidat pour conclure que cette valeur
    /// n'est pas affichee en chiffres : des vies en icones, une energie en jauge, un niveau
    /// en carte. Continuer a capturer depenserait des images pour rien, et le coordinateur
    /// desarme la partie sur ce signal. Le constat vaut d'etre remonte : il dit qu'il ne
    /// s'est rien passe, et pourquoi.
    /// </summary>
    public bool LikelyNotShownAsDigits => _consecutiveWithoutCandidate >= _options.NoCandidateAttemptsBeforeGivingUp;

    /// <summary>
    /// Une image a été examinée pour la valeur <paramref name="expectedValue"/>.
    ///
    /// Une reconnaissance ne compte que si elle tient les deux seuils. Une reconnaissance
    /// faible n'est pas une demi-preuve : elle est écartée, et elle reste dans le compte des
    /// tentatives, parce que le taux d'échec est lui-même une mesure utile.
    /// </summary>
    public void Record(long expectedValue, in VerificationOutcome outcome)
    {
        _attempts++;
        _maxCandidates = Math.Max(_maxCandidates, outcome.CandidateCount);

        // Zero candidat ne veut pas dire « pas reconnu » : cela veut dire qu'aucune suite de
        // chiffres de cette forme n'existe nulle part dans l'image. Repete, c'est le signe que
        // la valeur n'est pas ecrite en chiffres du tout.
        if (outcome.CandidateCount == 0)
        {
            _consecutiveWithoutCandidate++;
        }
        else
        {
            _consecutiveWithoutCandidate = 0;
        }
        if (!string.IsNullOrEmpty(outcome.VerifierVersion))
        {
            _verifierVersion = outcome.VerifierVersion;
        }

        if (!outcome.Matched)
        {
            return;
        }

        _confidenceSum += outcome.Confidence;
        _minConfidence = Math.Min(_minConfidence, outcome.Confidence);
        _stabilitySum += outcome.RegionStability;
        _stabilitySamples++;
        _matches++;

        // Une valeur n'entre au registre des preuves que si elle tient les deux seuils. La
        // stabilité de région est nulle à la première reconnaissance, par construction : il
        // n'y a pas encore de « fois précédente » à comparer, et cette première mesure ne
        // peut donc pas valoir comme preuve de champ.
        if (outcome.Confidence >= _options.MinConfidence && outcome.RegionStability >= _options.MinRegionStability)
        {
            _distinctMatched.Add(expectedValue);
        }
    }

    /// <summary>Ce que la partie a établi, sans aucune image.</summary>
    public DiscoverySessionEvidence Summarise()
    {
        var meanConfidence = _matches > 0 ? _confidenceSum / _matches : 0;
        var meanStability = _stabilitySamples > 0 ? _stabilitySum / _stabilitySamples : 0;
        var minConfidence = _matches > 0 ? _minConfidence : 0;

        var credible = _distinctMatched.Count >= _options.MinDistinctValues;
        var verdict = _attempts == 0
            ? "aucune tentative"
            : credible
                ? $"{_distinctMatched.Count} valeurs distinctes au meme endroit"
                : LikelyNotShownAsDigits
                    ? $"valeur probablement pas affichee en chiffres ({_consecutiveWithoutCandidate} tentatives sans candidat)"
                    : $"{_distinctMatched.Count} valeur(s) distincte(s), il en faut {_options.MinDistinctValues}";

        return new DiscoverySessionEvidence(
            _attempts,
            _matches,
            _distinctMatched.Count,
            Math.Round(meanConfidence, 4),
            Math.Round(minConfidence, 4),
            Math.Round(meanStability, 4),
            _maxCandidates,
            LikelyNotShownAsDigits,
            _verifierVersion,
            credible,
            verdict);
    }

    /// <summary>Nouvelle partie : les preuves d'une partie ne servent pas à la suivante.</summary>
    public void StartSession()
    {
        _distinctMatched.Clear();
        _attempts = 0;
        _matches = 0;
        _confidenceSum = 0;
        _minConfidence = double.MaxValue;
        _stabilitySum = 0;
        _stabilitySamples = 0;
        _maxCandidates = 0;
        _consecutiveWithoutCandidate = 0;
        _verifierVersion = string.Empty;
    }
}
