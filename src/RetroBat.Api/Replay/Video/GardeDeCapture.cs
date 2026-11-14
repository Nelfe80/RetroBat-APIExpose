namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE GARDE-FOU DE LA CAPTURE : filmer pendant la partie ne doit jamais ralentir le jeu, ni offrir un ralenti au
/// joueur. On suit la cadence du jeu (images du replay par seconde). La reference est la meilleure cadence
/// plausible vue depuis le debut, et jamais moins de 50 images par seconde (aucune machine suivie ne joue plus
/// lentement). Pauses et avance rapide ne comptent pas. Trois releves de suite nettement en dessous : la capture
/// doit s'arreter. Pur, pour etre teste.
/// <para>LA CADENCE ANNONCEE (14/11, 1.9.181) : le controle d'avant le film ne juge plus le processeur ni la cadence du
/// lancement. Une fois la partie stabilisee (<see cref="ControleAvantFilm.StabilisationS"/> apres le premier releve), un
/// releve sous 90 % de la cadence annoncee par le coeur est lent lui aussi : un jeu ralenti des le depart, que la
/// meilleure cadence vue ne trahit pas, arrete le film de cette partie.</para>
/// </summary>
public sealed class GardeDeCapture
{
    /// <summary>Sans cadence annoncee utilisable : seule la meilleure cadence vue sert de reference.</summary>
    public GardeDeCapture() : this(null) { }

    /// <summary>Avec la cadence annoncee par le coeur pour ce jeu (null, ou hors 40-75 images/s : ignoree).</summary>
    public GardeDeCapture(double? cadenceAnnoncee)
        => _annoncee = ControleAvantFilm.CadenceDeReference(cadenceAnnoncee);

    private readonly double? _annoncee;
    private double? _premierInstant;

    /// <summary>Sous 92 % de la cadence de reference, le releve est lent.</summary>
    public const double SeuilDeLenteur = 0.92;

    /// <summary>Releves lents de suite avant l'arret (un releve toutes les deux secondes environ).</summary>
    public const int RelevesLentsAvantArret = 3;

    /// <summary>La cadence la plus basse des machines suivies (PAL).</summary>
    private const double CadencePlancher = 50;

    /// <summary>Au-dela, c'est l'avance rapide (comme pour l'enregistreur des replays).</summary>
    private const double CadenceMax = 75;

    /// <summary>En dessous, le jeu est en pause ou dans le menu : rien a juger.</summary>
    private const double CadencePause = 20;

    private double _reference = CadencePlancher;
    private int _lentsDeSuite;
    private long? _derniereFrame;
    private double _dernierInstant;

    /// <summary>La cadence de reference actuelle.</summary>
    public double Reference => _reference;

    /// <summary>La cadence annoncee par le coeur, retenue comme seconde reference apres la stabilisation, ou null.</summary>
    public double? Annoncee => _annoncee;

    /// <summary>La derniere cadence jugee (images/s), ou null.</summary>
    public double? DerniereCadence { get; private set; }

    /// <summary>
    /// Un releve : l'instant (en secondes) et la frame du replay. Rend vrai quand la capture doit s'arreter.
    /// </summary>
    public bool Relever(double secondes, long frame)
    {
        if (_derniereFrame is null || frame < _derniereFrame || secondes <= _dernierInstant)
        {
            // Premier releve, ou compteur remis a zero (partie relancee) : la stabilisation repart d'ici.
            if (_derniereFrame is null || frame < _derniereFrame) _premierInstant = secondes;
            _derniereFrame = frame;
            _dernierInstant = secondes;
            return false;
        }
        var duree = secondes - _dernierInstant;
        if (duree < 0.5) return false;
        var cadence = (frame - _derniereFrame.Value) / duree;
        _derniereFrame = frame;
        _dernierInstant = secondes;

        if (cadence < CadencePause || cadence > CadenceMax)
        {
            _lentsDeSuite = 0;
            return false;
        }
        DerniereCadence = cadence;
        if (cadence > _reference) _reference = cadence;
        var seuil = _reference * SeuilDeLenteur;
        if (_annoncee is { } annoncee && secondes - (_premierInstant ?? secondes) >= ControleAvantFilm.StabilisationS)
            seuil = Math.Max(seuil, annoncee * ControleAvantFilm.CadenceMinRatio);
        _lentsDeSuite = cadence < seuil ? _lentsDeSuite + 1 : 0;
        return _lentsDeSuite >= RelevesLentsAvantArret;
    }
}
