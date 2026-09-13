using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Le chef d'orchestre de la découverte silencieuse, pour une partie.
///
/// Il ne touche ni tuyau ni processus : il reçoit deux canaux et il les fait jouer ensemble.
/// C'est ce qui permet de vérifier l'enchaînement complet, y compris les cas où il ne doit
/// rien se passer, sans lancer un émulateur.
///
/// Sa règle tient en peu de mots. Une partie commence : on décide d'un mode et, s'il n'est pas
/// la découverte, on n'arme rien, jamais, jusqu'à la partie suivante. En découverte, chaque
/// changement du score agrégé passe par le déclencheur ; quand celui-ci dit oui, on annonce la
/// valeur au vérificateur PUIS on arme l'émulateur, dans cet ordre, parce qu'une image qui
/// arriverait sans attente serait jetée sans être regardée. Les résultats nourrissent le suivi.
/// La partie finit : on demande l'oubli, on désarme, et on rend ce qui a été établi.
///
/// Deux choses qu'il ne fait pas, et c'est voulu : il ne signe rien, et il ne parle pas au
/// réseau. Ce qu'il rend est une constatation, que le reporter transformera en enveloppe.
/// </summary>
public sealed class ScoringDiscoveryCoordinator
{
    private readonly ScoringDiscoveryOptions _options;
    private readonly IEmulatorCaptureChannel _emulator;
    private readonly IScoreVerifierChannel _verifier;
    private readonly ILogger? _logger;

    private readonly CaptureTrigger _trigger;
    private readonly DiscoveryTracker _tracker;
    private readonly object _gate = new();

    private ScoringModeDecision _decision = new(ScoringMode.Disabled, "aucune partie");
    private string _token = string.Empty;
    private long _expectedValue;
    private FrameOrientationDegrees _orientation;
    private bool _sessionOpen;

    public ScoringDiscoveryCoordinator(
        ScoringDiscoveryOptions options,
        IEmulatorCaptureChannel emulator,
        IScoreVerifierChannel verifier,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _logger = logger;
        _trigger = new CaptureTrigger(_options);
        _tracker = new DiscoveryTracker(_options);
        _verifier.ResultReceived += OnResult;
    }

    public ScoringModeDecision Mode => _decision;

    public bool Observing => _sessionOpen && _decision.CapturesFrames && !_trigger.DisarmedForSession;

    /// <summary>Captures demandées depuis le début de la partie.</summary>
    public int ArmedCount { get; private set; }

    /// <summary>
    /// Une partie commence. Les conditions sont résolues ici, une fois : rien ne les
    /// réexamine ensuite, et un mode ne change pas au milieu d'une partie.
    /// </summary>
    public async Task StartSessionAsync(
        ScoringConditions conditions,
        FrameOrientationDegrees orientation,
        string token,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _decision = ScoringModeResolver.Resolve(conditions);
            _orientation = orientation;
            _token = token;
            _expectedValue = 0;
            _sessionOpen = true;
            ArmedCount = 0;
            _trigger.StartSession();
            _tracker.StartSession();
        }

        _logger?.LogInformation("Decouverte du scoring : {Decision}", _decision);

        // L'oubli se demande même hors découverte : le vérificateur a pu servir la partie
        // précédente, et sa police ne vaut rien pour celle-ci.
        if (_verifier.IsAvailable)
        {
            await _verifier.ForgetAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Le score agrégé a changé (score.live.changed).</summary>
    public void OnScoreChanged(long score, DateTimeOffset now)
    {
        if (!Observing)
        {
            return;
        }

        _trigger.OnScoreChanged(score, now);
    }

    /// <summary>
    /// Le temps passe. Arme si le déclencheur le dit, et dans le bon ordre : la valeur
    /// d'abord, la capture ensuite.
    /// </summary>
    public async Task PollAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Observing)
        {
            return;
        }

        // La valeur n'est pas écrite en chiffres : on cesse pour cette partie plutôt que de
        // dépenser des images pour rien.
        if (_tracker.LikelyNotShownAsDigits)
        {
            _trigger.DisarmForSession("valeur pas affichee en chiffres");
            await _emulator.DisarmAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Decouverte du scoring : {Reason}", _trigger.DisarmReason);
            return;
        }

        var decision = _trigger.Poll(now);
        if (!decision.Arm)
        {
            return;
        }

        if (!await _verifier.ExpectAsync(_token, decision.Value, _orientation, cancellationToken).ConfigureAwait(false))
        {
            // Sans attente posée, une image qui arriverait serait jetée : autant ne pas la
            // demander.
            _trigger.DisarmForSession("verificateur injoignable");
            _logger?.LogWarning("Decouverte du scoring : verificateur injoignable, arret pour cette partie.");
            return;
        }

        lock (_gate)
        {
            _expectedValue = decision.Value;
        }

        if (!await _emulator.ArmAsync(_token, decision.Frames, cancellationToken).ConfigureAwait(false))
        {
            _trigger.DisarmForSession("emulateur n'a pas pu s'armer");
            _logger?.LogWarning("Decouverte du scoring : capture indisponible, arret pour cette partie.");
            return;
        }

        ArmedCount++;
    }

    /// <summary>La durée d'image de l'émulateur a dérivé de tant de pour cent.</summary>
    public async Task ReportFrameTimeDriftAsync(double percent, CancellationToken cancellationToken)
    {
        if (!Observing)
        {
            return;
        }

        _trigger.ReportFrameTimeDrift(percent);
        if (_trigger.DisarmedForSession)
        {
            await _emulator.DisarmAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Decouverte du scoring : {Reason}", _trigger.DisarmReason);
        }
    }

    /// <summary>
    /// La partie se termine. Rend ce qui a été établi, ou null si cette partie n'avait rien
    /// à établir : c'est ce que le reporter attend pour décider s'il y a une enveloppe.
    /// </summary>
    public async Task<DiscoverySessionEvidence?> EndSessionAsync(CancellationToken cancellationToken)
    {
        bool wasObserving;
        lock (_gate)
        {
            wasObserving = _sessionOpen && _decision.CapturesFrames;
            _sessionOpen = false;
        }

        if (_verifier.IsAvailable)
        {
            await _verifier.ForgetAsync(cancellationToken).ConfigureAwait(false);
        }

        await _emulator.DisarmAsync(cancellationToken).ConfigureAwait(false);

        if (!wasObserving)
        {
            return null;
        }

        var evidence = _tracker.Summarise();
        _logger?.LogInformation(
            "Decouverte du scoring : {Attempts} tentative(s), {Matches} reconnue(s), {Distinct} valeur(s) distincte(s) : {Verdict}",
            evidence.Attempts, evidence.Matches, evidence.DistinctValuesMatched, evidence.Verdict);
        return evidence;
    }

    private void OnResult(string token, VerificationOutcome outcome)
    {
        long expected;
        lock (_gate)
        {
            // Un résultat qui porte le jeton d'une autre partie est en retard sur nous : le
            // compter fausserait les preuves de la partie en cours.
            if (!_sessionOpen || !string.Equals(token, _token, StringComparison.Ordinal))
            {
                return;
            }

            expected = _expectedValue;
        }

        _tracker.Record(expected, outcome);
    }
}
