namespace RetroBat.Api.Scoring.Discovery;

/// <summary>Ce que le déclencheur décide à un instant donné.</summary>
public readonly record struct CaptureDecision(bool Arm, long Value, int Frames, string Reason)
{
    public static CaptureDecision Wait(string reason) => new(false, 0, 0, reason);

    public override string ToString() => Arm ? $"armer {Frames} image(s) pour {Value}" : $"attendre ({Reason})";
}

/// <summary>
/// Décide quand armer la capture, et surtout quand ne pas l'armer.
///
/// Le contrat d'A1.11 tient en une phrase : hors déclenchement, rien ne se copie. Cette classe
/// est l'endroit où cette phrase devient une règle, et elle ne fait que cela : elle reçoit des
/// changements de score et le temps qui passe, elle rend une décision. Aucune entrée-sortie,
/// aucun émulateur, aucun tuyau, donc une règle entièrement vérifiable.
///
/// Quatre freins, tous mesurés en temps et non en images, parce qu'un jeu ne tourne pas
/// toujours à 60 Hz :
///
///   la stabilité   un score qui bouge encore n'est pas un score : on attend qu'il se pose ;
///   l'intervalle   deux captures ne se suivent pas de plus près que MinIntervalMs ;
///   le plafond     au-delà de MaxCapturesPerMinute, on se tait pour la minute en cours ;
///   la dérive      si la durée d'image de l'émulateur s'allonge, on s'arrête pour la partie.
///
/// Le dernier frein est le seul qui ne se rouvre pas tout seul : une partie qui a ralenti une
/// fois ne redeviendra pas un bon terrain de mesure à la minute suivante, et la partie du
/// joueur passe avant la mesure.
/// </summary>
public sealed class CaptureTrigger
{
    private readonly ScoringDiscoveryOptions _options;

    private long? _pendingValue;
    private DateTimeOffset _pendingSince;
    private DateTimeOffset? _lastCapture;
    private DateTimeOffset _minuteWindowStart;
    private int _capturesThisMinute;
    private bool _disarmedForSession;
    private string _disarmReason = string.Empty;

    public CaptureTrigger(ScoringDiscoveryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Vrai quand plus rien ne sera armé jusqu'à la fin de la partie.</summary>
    public bool DisarmedForSession => _disarmedForSession;

    public string DisarmReason => _disarmReason;

    /// <summary>Captures armées depuis le début de la minute en cours.</summary>
    public int CapturesThisMinute => _capturesThisMinute;

    /// <summary>
    /// Le score agrégé a changé. On ne décide rien ici : un score qui bouge est un score qui
    /// n'a pas fini de bouger, et le chiffre affiché à l'écran est peut-être encore en train
    /// de défiler.
    /// </summary>
    public void OnScoreChanged(long value, DateTimeOffset now)
    {
        if (_disarmedForSession || value < 0)
        {
            return;
        }

        _pendingValue = value;
        _pendingSince = now;
    }

    /// <summary>
    /// Le temps passe. Rend une décision d'armement quand la valeur en attente s'est posée et
    /// qu'aucun frein ne s'y oppose.
    /// </summary>
    public CaptureDecision Poll(DateTimeOffset now)
    {
        if (_disarmedForSession)
        {
            return CaptureDecision.Wait(_disarmReason);
        }

        if (_pendingValue is not { } value)
        {
            return CaptureDecision.Wait("rien en attente");
        }

        if ((now - _pendingSince).TotalMilliseconds < _options.StableDelayMs)
        {
            return CaptureDecision.Wait("score pas encore pose");
        }

        if (_lastCapture is { } last && (now - last).TotalMilliseconds < _options.MinIntervalMs)
        {
            return CaptureDecision.Wait("trop tot apres la capture precedente");
        }

        if (now - _minuteWindowStart >= TimeSpan.FromMinutes(1))
        {
            _minuteWindowStart = now;
            _capturesThisMinute = 0;
        }

        if (_capturesThisMinute >= _options.MaxCapturesPerMinute)
        {
            return CaptureDecision.Wait("plafond de la minute atteint");
        }

        // La valeur est consommée : le même score ne déclenche pas deux fois. Il faudra qu'il
        // change à nouveau, ce qui est exactement ce que le suivi temporel veut observer.
        _pendingValue = null;
        _lastCapture = now;
        _capturesThisMinute++;
        if (_capturesThisMinute == 1)
        {
            _minuteWindowStart = now;
        }

        return new CaptureDecision(true, value, _options.MaxFramesPerTrigger, "score pose");
    }

    /// <summary>
    /// La durée moyenne d'image mesurée pendant la partie, comparée à celle d'avant tout
    /// armement. Au-delà du seuil, la capture s'arrête pour la partie : la partie du joueur
    /// passe avant la mesure.
    /// </summary>
    public void ReportFrameTimeDrift(double percent)
    {
        if (_disarmedForSession || percent <= _options.FrameTimeDriftMaxPercent)
        {
            return;
        }

        DisarmForSession($"derive de duree d'image {percent:F1} % au-dela de {_options.FrameTimeDriftMaxPercent} %");
    }

    /// <summary>Arrêt définitif pour cette partie, pour une raison nommée.</summary>
    public void DisarmForSession(string reason)
    {
        _disarmedForSession = true;
        _disarmReason = reason;
        _pendingValue = null;
    }

    /// <summary>Nouvelle partie : tout repart, y compris un désarmement définitif.</summary>
    public void StartSession()
    {
        _pendingValue = null;
        _lastCapture = null;
        _capturesThisMinute = 0;
        _minuteWindowStart = default;
        _disarmedForSession = false;
        _disarmReason = string.Empty;
    }
}
