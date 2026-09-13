namespace RetroBat.Api.Scoring.Discovery;

/// <summary>
/// Les conditions sous lesquelles cette borne participe à la découverte silencieuse du scoring.
///
/// Quatre conditions doivent tenir ensemble pour qu'une image soit un jour capturée, et
/// celle-ci n'est que la première : il faut aussi que la machine soit appairée, que le
/// vérificateur et le composant de capture soient présents, et que la plateforme ait enrôlé
/// la borne. Cette clé est celle du joueur, et elle est réelle : une mesure qu'on ne peut pas
/// éteindre n'est pas une mesure. Même règle que <c>NelfePlay:PlayReportingEnabled</c>.
///
/// Elle est à <c>true</c> par défaut : rien de visuel ne quitte la machine (seuls cinq nombres
/// remontent), et la qualification d'un jeu demande plusieurs bornes distinctes, qu'un
/// réglage éteint au départ n'atteindrait jamais.
/// </summary>
public sealed class ScoringDiscoveryOptions
{
    /// <summary>
    /// Clé du joueur : <c>ApiExpose:NelfePlay:ScoringDiscovery:Enabled</c>, reflétée dans
    /// EmulationStation sous <c>global.apiexpose.nelfeplay.scoring_discovery</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Nom du tuyau par lequel APIExpose parle au vérificateur et lit ses résultats.
    /// Aucune image n'y passe.
    /// </summary>
    public string VerifierPipeName { get; set; } = "RetroBatVerifierPipe";

    /// <summary>
    /// Nom du tuyau par lequel l'émulateur dépose ses images, tenu par le vérificateur.
    /// APIExpose ne l'ouvre jamais : il ne fait que le nommer.
    /// </summary>
    public string FramesPipeName { get; set; } = "RetroBatFramesPipe";

    /// <summary>
    /// Nom du tuyau par lequel APIExpose arme la capture dans le wrapper libretro.
    /// Texte seul.
    /// </summary>
    public string WrapperControlPipeName { get; set; } = "RetroBatFramesControlPipe";

    /// <summary>Port d'écoute du plugin Lua de MAME. Distinct du pont RAM (12347).</summary>
    public int MameControlPort { get; set; } = 12348;

    /// <summary>
    /// Délai de stabilité du score avant d'armer, en millisecondes (OCR_STABLE_DELAY_MS).
    /// </summary>
    public int StableDelayMs { get; set; } = 300;

    /// <summary>Images demandées par déclenchement (OCR_MAX_FRAMES_PER_TRIGGER).</summary>
    public int MaxFramesPerTrigger { get; set; } = 3;

    /// <summary>Intervalle minimal entre deux captures, en millisecondes (OCR_MIN_INTERVAL_MS).</summary>
    public int MinIntervalMs { get; set; } = 500;

    /// <summary>Captures au plus par minute (OCR_MAX_CAPTURES_PER_MINUTE).</summary>
    public int MaxCapturesPerMinute { get; set; } = 60;

    /// <summary>
    /// Dérive de durée d'image, en pourcentage, au-delà de laquelle la capture se désarme
    /// pour la session (OCR_FRAME_TIME_DRIFT_MAX_PERCENT). La partie passe avant la mesure.
    /// </summary>
    public int FrameTimeDriftMaxPercent { get; set; } = 5;

    /// <summary>Confiance minimale pour qu'une reconnaissance compte (OCR_MIN_CONFIDENCE).</summary>
    public double MinConfidence { get; set; } = 0.98;

    /// <summary>
    /// Stabilité de région minimale (OCR_MIN_REGION_STABILITY) : le nombre doit se lire au
    /// même endroit d'une fois sur l'autre, sinon ce n'est pas le même champ.
    /// </summary>
    public double MinRegionStability { get; set; } = 0.95;

    /// <summary>
    /// Valeurs distinctes qu'il faut avoir reconnues, au même endroit, pour qu'une partie
    /// vaille comme preuve (§7.2.6). Deux ne prouvent rien : un score qui passe de 0 à 100
    /// pourrait coïncider avec n'importe quel compteur.
    /// </summary>
    public int MinDistinctValues { get; set; } = 3;

    /// <summary>
    /// Tentatives consécutives sans le moindre candidat au bout desquelles on cesse d'armer
    /// pour cette partie : la valeur n'est probablement pas affichée en chiffres.
    ///
    /// Beaucoup de jeux montrent les vies par des icônes, l'énergie par une jauge, le niveau
    /// par une carte. Aucune image ne contiendra jamais ces nombres, et continuer à capturer
    /// ne ferait que dépenser des images pour rien. Deux rafales complètes suffisent à le
    /// constater, et le constat est lui-même une information de qualification.
    /// </summary>
    public int NoCandidateAttemptsBeforeGivingUp { get; set; } = 6;
}
