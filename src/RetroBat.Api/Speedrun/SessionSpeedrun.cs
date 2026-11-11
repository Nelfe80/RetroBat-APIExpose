using System.Text.Json.Serialization;

namespace RetroBat.Api.Speedrun;

/// <summary>Une action que le HUD peut demander, permise ou non, et pourquoi (§11 bis.4 du prototype Sonic).</summary>
public sealed record ActionPermise(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("allowed")] bool Permise,
    [property: JsonPropertyName("reason")] string? Raison,
    [property: JsonPropertyName("confirm")] bool Confirmation = false);

/// <summary>Une commande du HUD ou de la manette : idempotente par <c>RequestId</c>, refusee si la session est perimee.</summary>
public sealed record CommandeSpeedrun(string RequestId, string SessionId, string Action, bool Confirme = false, string? Argument = null);

/// <summary>La reponse a une commande : accepted, rejected ou pending, l'etat decide et la sequence.</summary>
public sealed record ReponseCommande(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Statut,
    [property: JsonPropertyName("reason")] string? Raison,
    [property: JsonPropertyName("state")] string Etat,
    [property: JsonPropertyName("sequence")] long Sequence);

/// <summary>Ce que la session demande a la borne apres une commande.</summary>
public enum EffetDemande { Aucun, Relancer }

/// <summary>Un evenement du contrat (type « speedrun.xxx »), avec la sequence de la session.</summary>
public sealed record EvenementSession(string Type, long Sequence, object Donnees);

/// <summary>Une tentative finie et classable, avec son horloge : la matiere du passeport (§11).</summary>
public sealed record TentativeFinie(string AttemptId, int Numero, ResultatTentative Resultat, long DepartMs, long ArriveeMs);

/// <summary>Un message au joueur dans RetroArch : sa cle du dictionnaire, son texte par defaut et ses valeurs.</summary>
public sealed record MessageJoueur(string Cle, string ParDefaut, params (string Nom, string Valeur)[] Valeurs);

/// <summary>Le depart publie au HUD : frame, horloge de la borne a cet instant, cadence du coeur (§11 bis.2).</summary>
public sealed record DepartPublie(
    [property: JsonPropertyName("frame")] long Frame,
    [property: JsonPropertyName("clockMs")] long? HorlogeMs,
    [property: JsonPropertyName("rate")] double Cadence);

/// <summary>Un chrono publie : frames et affichage a la cadence du coeur ; provisoire tant que la tentative n'est pas finie.</summary>
public sealed record ChronoPublie(
    [property: JsonPropertyName("frames")] long? Frames,
    [property: JsonPropertyName("display")] string? Affichage,
    [property: JsonPropertyName("provisional")] bool Provisoire);

/// <summary>La ligne de demonstration du prototype : les deux chronos, le temps du jeu et leur controle.</summary>
public sealed record DemonstrationPubliee(
    [property: JsonPropertyName("aligned")] ChronoPublie? Cale,
    [property: JsonPropertyName("independent")] ChronoPublie? Independant,
    [property: JsonPropertyName("gameTimeSeconds")] int? TempsDuJeu,
    [property: JsonPropertyName("gameTimeCheck")] string Controle);

/// <summary>Les comparaisons : le meilleur temps du joueur sur ce defi (journal local). Le record vient en S4.</summary>
public sealed record ComparaisonsPubliees(
    [property: JsonPropertyName("selected")] string Choisie,
    [property: JsonPropertyName("personalBest")] ChronoPublie? MeilleurTemps);

/// <summary>Le snapshot de session (§11 bis.5), a la connexion du HUD et apres chaque changement.</summary>
public sealed record InstantaneSpeedrun(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("attemptId")] string AttemptId,
    [property: JsonPropertyName("attemptNumber")] int Numero,
    [property: JsonPropertyName("challengeId")] string DefiId,
    [property: JsonPropertyName("rulesVersion")] int VersionDuReglement,
    [property: JsonPropertyName("state")] string Etat,
    [property: JsonPropertyName("cause")] string? Cause,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("start")] DepartPublie? Depart,
    [property: JsonPropertyName("official")] ChronoPublie? Officiel,
    [property: JsonPropertyName("splits")] IReadOnlyList<object> Splits,
    [property: JsonPropertyName("comparisons")] ComparaisonsPubliees Comparaisons,
    [property: JsonPropertyName("allowedActions")] IReadOnlyList<ActionPermise> ActionsPermises,
    [property: JsonPropertyName("demo")] DemonstrationPubliee? Demonstration,
    // Les compteurs de la tentative (coups recus de Metal Slug 3 facilite, 03/11) ; vide sans compteur au reglement.
    [property: JsonPropertyName("counters")] IReadOnlyDictionary<string, int>? Compteurs = null,
    // (11/11, lot LS2 du Deck) les fantomes qui courent : { next, reference, list[{ id, label, tint, time_ms, status, role,
    // splits[{ segment, ms }] }] } ; null sans fantome. Pose par le service, pas par la session.
    [property: JsonPropertyName("ghosts")] FantomesPublies? Fantomes = null);

/// <summary>
/// UNE SESSION DE SPEEDRUN SUR LA BORNE (lot S3 du prototype Sonic) : un defi, un jeu lance, des tentatives
/// successives. Domaine pur : ni reseau, ni fichier, ni horloge propre. Le service la nourrit des evenements
/// du wrapper groupes par image, des valeurs relevees et des commandes, et publie ce qu'elle rend.
///
/// Etats publies au HUD (§9.1) : IDLE (preparee), ARMED, RUNNING (en course, arrivee provisoire), FINISHED,
/// ABORTED, INVALID (ratee ou annulee, avec la cause), RESTARTING.
///
/// UNE TENTATIVE CLOSE RESTE AFFICHEE jusqu'au depart suivant : un evaluateur neuf attend en coulisse et
/// ne devient la tentative courante, avec un nouvel identifiant, qu'a son depart. Sur Green Hill 1, les
/// conditions de depart (zone 0, acte 0) sont vraies des l'ecran titre : sans cela, le temps d'arrivee
/// disparaitrait dans l'image qui suit. Seule une relance ouvre aussitot une tentative neuve.
/// </summary>
public sealed class SessionSpeedrun
{
    public const string Idle = "IDLE", Armed = "ARMED", Running = "RUNNING", Finished = "FINISHED",
        Aborted = "ABORTED", Invalid = "INVALID", Restarting = "RESTARTING";

    public const string ActionRelancer = "run.restart", ActionAbandonner = "run.abort", ActionComparaison = "comparison.set";

    /// <summary>Les actions d'un chronometre d'entrainement, refusees en defi classe (§11 bis.4).</summary>
    public static readonly IReadOnlyList<string> ActionsDEntrainement =
        ["run.start", "run.split", "run.undo_split", "run.skip_split", "run.pause", "run.resume"];

    /// <summary>Les comparaisons connues du contrat ; seul le meilleur temps du joueur existe avant S4.</summary>
    public static readonly IReadOnlyList<string> Comparaisons = ["pb", "record", "best_segments", "rival"];

    private const int ReponsesGardees = 64;

    private readonly Reglement _r;
    private readonly Func<string> _nouvelId;
    private readonly Dictionary<string, ReponseCommande> _reponses = new(StringComparer.Ordinal);
    private readonly Queue<string> _ordreDesReponses = new();
    private readonly List<EvenementSession> _aPublier = new();
    private readonly List<(string AttemptId, ResultatTentative Resultat)> _closes = new();
    private readonly List<MessageJoueur> _messages = new();
    private bool _departAnnonce;
    private bool _tricheSignalee;
    private TentativeFinie? _meilleure;
    private long? _horlogeDArrivee;
    private Evaluateur _courante;
    private Evaluateur? _suivante;
    private EtatTentative _etatVu = EtatTentative.Preparee;
    private bool _relance;
    private long? _horlogeDuDepart;
    private long? _horlogeDeLImage;
    private string _comparaison = "pb";

    public SessionSpeedrun(string defiId, Reglement reglement, Func<string>? nouvelId = null)
    {
        DefiId = defiId;
        _r = reglement;
        _nouvelId = nouvelId ?? (() => Guid.NewGuid().ToString("N"));
        SessionId = _nouvelId();
        _courante = new Evaluateur(reglement);
        AttemptId = _nouvelId();
        Numero = 1;
    }

    public string DefiId { get; }

    public Reglement Reglement => _r;

    public string SessionId { get; }

    public string AttemptId { get; private set; }

    public int Numero { get; private set; }

    public long Sequence { get; private set; }

    /// <summary>Le meilleur temps du joueur sur ce defi, en frames (journal local) ; null sans temps fini.</summary>
    public long? MeilleurTemps { get; set; }

    /// <summary>(05/11) Le chrono independant de ce meilleur temps : du depart a l'arrivee du run, la duree de son fantome.</summary>
    public long? MeilleurIndependant { get; private set; }

    /// <summary>
    /// (10/11) L'arrivee de la tentative qui vient de se fermer (frames officielles, chrono independant) ; null si elle n'est pas
    /// arrivee ou pas classable. C'est elle qui devient un fantome, jamais le meilleur temps du journal (MeilleurTemps) :
    /// depuis le 05/11, le temps du journal etait passe a la place, et le replay d'une course plus lente devenait « mon record ».
    /// </summary>
    public (long Images, long? Independant)? DerniereArrivee { get; private set; }

    public string Etat => _relance ? Restarting : EtatHud(_courante.Etat);

    public static string EtatHud(EtatTentative etat) => etat switch
    {
        EtatTentative.Preparee => Idle,
        EtatTentative.Armee => Armed,
        EtatTentative.EnCourse or EtatTentative.ArriveeProvisoire => Running,
        EtatTentative.Finie => Finished,
        EtatTentative.Abandonnee => Aborted,
        _ => Invalid,
    };

    /// <summary>Les tentatives parties puis closes depuis le dernier appel : le service les ecrit au journal local.</summary>
    public IReadOnlyList<(string AttemptId, ResultatTentative Resultat)> PrendreLesCloses()
    {
        var c = _closes.ToList();
        _closes.Clear();
        return c;
    }

    /// <summary>Le meilleur temps classable de ce lancement, une seule fois : lui seul part au site (decision du 30/10).</summary>
    public TentativeFinie? PrendreLaMeilleure()
    {
        var m = _meilleure;
        _meilleure = null;
        return m;
    }

    private TimeSpan? _relanceAutomatique;

    /// <summary>
    /// RELANCE AUTOMATIQUE (demandes du user du 04/11 : « un speedrun niveau devrait nous renvoyer au debut du niveau et remettre
    /// le temps a 0 », et a l'arrivee aussi « un retour au debut tant que le joueur ne sort pas ») : un speedrun parti sur un
    /// etat (niveau, boss, monde) repart seul apres un echec ou une arrivee ; le service la commande au bout de ce delai. Rendu
    /// une fois par tentative.
    /// </summary>
    public TimeSpan? PrendreLaRelanceAutomatique()
    {
        var r = _relanceAutomatique;
        _relanceAutomatique = null;
        return r;
    }

    /// <summary>Le temps avant de repartir : assez pour voir sa mort, ou son temps a l'arrivee. Null : pas de relance. Pur.</summary>
    /// <remarks>Une tentative annulee (ralentissement, avance rapide, mode Debug...) repart aussi, apres que son message a ete lu
    /// (05/11 : annulee pour ralentissement, la partie avait enchaine sur le niveau suivant) ; pas apres une pause : la relance
    /// se ferait dans RetroArch en pause, le joueur la commande en revenant.</remarks>
    internal static TimeSpan? DelaiDeRelance(Reglement r, EtatTentative etat, string? cause = null) => !SeRelanceSeul(r) ? null : etat switch
    {
        EtatTentative.Ratee => TimeSpan.FromSeconds(1.5),
        EtatTentative.Finie => TimeSpan.FromSeconds(3),
        EtatTentative.Annulee when cause != "pause" => TimeSpan.FromSeconds(3),
        _ => null,
    };

    /// <summary>Un speedrun parti sur un etat de depart : le recharger renvoie au debut de l'epreuve.</summary>
    internal static bool SeRelanceSeul(Reglement r) => r.Kind.StartsWith("speedrun_", StringComparison.Ordinal) && r.Etat is not null;

    /// <summary>Les messages au joueur depuis le dernier appel : le service les affiche dans RetroArch.</summary>
    public IReadOnlyList<MessageJoueur> PrendreLesMessages()
    {
        var m = _messages.ToList();
        _messages.Clear();
        return m;
    }

    /// <summary>Les evenements a publier depuis le dernier appel, dans l'ordre de leur sequence.</summary>
    public IReadOnlyList<EvenementSession> PrendreLesEvenements()
    {
        var e = _aPublier.ToList();
        _aPublier.Clear();
        return e;
    }

    /// <summary>Les adresses a relever directement (READ_CORE_RAM) pour la tentative courante et celle qui attend.</summary>
    public IReadOnlyList<long> AdressesInconnues
    {
        get
        {
            if (_relance) return [];
            var a = new List<long>();
            if (!_courante.EstTerminee) a.AddRange(_courante.AdressesInconnues);
            if (_suivante is not null) a.AddRange(_suivante.AdressesInconnues);
            return a.Distinct().ToList();
        }
    }

    public void Relever(long adresse, int valeur)
    {
        if (_relance) return;
        if (!_courante.EstTerminee) _courante.Relever(adresse, valeur);
        _suivante?.Relever(adresse, valeur);
        Constater();
    }

    /// <summary>Les evenements d'une image du wrapper, d'un bloc ; <paramref name="horlogeMs"/> : l'horloge de leur emission.</summary>
    public void RecevoirImage(IReadOnlyList<Signal> image, long? horlogeMs = null)
    {
        if (_relance || image.Count == 0) return;
        _horlogeDeLImage = horlogeMs ?? image[0].HorlogeMs;
        if (!_courante.EstTerminee)
        {
            var avant = _r.Compteurs.Count == 0 ? null : new Dictionary<string, int>(_courante.Compteurs);
            _courante.RecevoirImage(image);
            if (avant is not null) AnnoncerLesCoups(avant);
            Constater();
            return;
        }

        // La tentative affichee est close : la suivante recoit l'image, et devient courante a son depart.
        _suivante ??= new Evaluateur(_r);
        _suivante.RecevoirImage(image);
        if (_suivante.Etat is EtatTentative.EnCourse or EtatTentative.ArriveeProvisoire or EtatTentative.Finie or EtatTentative.Ratee
            or EtatTentative.Annulee)
        {
            Promouvoir(_suivante);
            _suivante = null;
        }

        Constater();
    }

    /// <summary>
    /// LE COMPTEUR A L'ECRAN (Metal Slug 3 facilite, 03/11) : un compteur a maximum ou a penalite qui monte se dit au joueur,
    /// « Touche ! 97 restants, +1 s ». Le dernier coup permis ne s'annonce pas : la tentative echoue et le dit elle-meme.
    /// </summary>
    private void AnnoncerLesCoups(IReadOnlyDictionary<string, int> avant)
    {
        foreach (var c in _r.Compteurs)
        {
            var n = _courante.Compteurs.TryGetValue(c.Id, out var v) ? v : 0;
            if (n <= (avant.TryGetValue(c.Id, out var a) ? a : 0) || _courante.EstTerminee) continue;
            // LE COMPTEUR EN DIRECT (PlayerDeck H-D23, 08/11) : un evenement de la session, donc un instantane republie ; sans
            // lui, « counters » ne changeait dans l'instantane qu'au changement d'etat. Affichage seulement : rien n'est
            // ecrit dans le resultat de la tentative ni dans le passeport.
            Publier("speedrun.counter", new
            {
                sessionId = SessionId, attemptId = AttemptId, id = c.Id, value = n, max = c.Maximum,
                left = c.Maximum is { } m ? Math.Max(0, m + 1 - n) : (int?)null,
                penaltyFrames = c.PenaliteFrames is { } pf and > 0 ? (long)pf * n : 0L,
            });
            if (c.Maximum is null && c.PenaliteFrames is not > 0) continue;
            var restants = c.Maximum is { } max ? Math.Max(0, max + 1 - n) : (int?)null;
            var penalite = c.PenaliteFrames is { } p and > 0
                ? (p / _r.Cadence).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : null;
            _messages.Add(new MessageJoueur(
                restants is null ? "speedrun.msg.hit_penalty" : penalite is null ? "speedrun.msg.hit_left" : "speedrun.msg.hit",
                restants is null ? "Hit! +{penalty} s" : penalite is null ? "Hit! {left} left" : "Hit! {left} left, +{penalty} s",
                ("left", restants?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""), ("penalty", penalite ?? "")));
        }
    }

    /// <summary>Un controle de la borne (fast_forward, save_state_load...) : annule la tentative en course si le reglement l'interdit.</summary>
    public void Controle(string type, long? frame = null)
    {
        if (_relance || _courante.EstTerminee) return;
        _courante.Controle(type, frame);
        Constater();
    }

    public void CapteurCoupe(long adresse)
    {
        if (_relance || _courante.EstTerminee) return;
        _courante.CapteurCoupe(adresse);
        Constater();
    }

    /// <summary>Le jeu se ferme (Hotkey + Start, ou fin de l'emulateur) : la tentative partie est abandonnee.</summary>
    public void JeuFerme(string cause = "game_closed")
    {
        _relance = false;
        if (!_courante.EstTerminee) _courante.Abandonner(cause);
        Constater();
    }

    /// <summary>
    /// UNE COMMANDE, idempotente par requestId : la meme demande rend la meme reponse, sans rien refaire. Deux
    /// demandes de relance rapprochees ne creent jamais deux tentatives : la seconde trouve la relance en cours.
    /// </summary>
    public ReponseCommande Commander(CommandeSpeedrun c, out EffetDemande effet)
    {
        effet = EffetDemande.Aucun;
        if (_reponses.TryGetValue(c.RequestId, out var deja)) return deja;

        ReponseCommande Rendre(string statut, string? raison)
        {
            var r = new ReponseCommande(c.RequestId, statut, raison, Etat, Sequence);
            _reponses[c.RequestId] = r;
            _ordreDesReponses.Enqueue(c.RequestId);
            while (_ordreDesReponses.Count > ReponsesGardees) _reponses.Remove(_ordreDesReponses.Dequeue());
            return r;
        }

        if (!string.Equals(c.SessionId, SessionId, StringComparison.Ordinal)) return Rendre("rejected", "stale_session");
        var permise = ActionsPermises().FirstOrDefault(a => a.Action == c.Action);
        if (permise is null && !c.Action.StartsWith("comparison.", StringComparison.Ordinal)) return Rendre("rejected", "unknown_action");
        if (permise is { Permise: false }) return Rendre("rejected", permise.Raison);
        if (permise is { Confirmation: true } && !c.Confirme) return Rendre("rejected", "confirm_required");

        switch (c.Action)
        {
            case ActionRelancer:
                if (!_courante.EstTerminee) _courante.Abandonner("restart");
                Constater();
                _relance = true;
                _suivante = null;
                Publier("speedrun.state", new { sessionId = SessionId, attemptId = AttemptId, state = Etat });
                effet = EffetDemande.Relancer;
                return Rendre("pending", null);

            case ActionAbandonner:
                _courante.Abandonner("abandoned");
                Constater();
                return Rendre("accepted", null);

            case ActionComparaison:
                if (c.Argument is not { } choix || !Comparaisons.Contains(choix)) return Rendre("rejected", "unknown_comparison");
                if (choix != "pb") return Rendre("rejected", "not_available_yet");
                _comparaison = choix;
                Publier("speedrun.comparison", new { sessionId = SessionId, selected = choix });
                return Rendre("accepted", null);
        }

        return Rendre("rejected", "unknown_action");
    }

    /// <summary>
    /// LE JEU EST REPLACE AU DEPART (RESET envoye) : une tentative neuve, un nouvel identifiant. Elle ne s'arme
    /// que sur les conditions de depart du reglement et ne part que sur son evenement : le depart reste verifie.
    /// </summary>
    public void RelanceFaite()
    {
        if (!_relance) return;
        _relance = false;
        Ouvrir(new Evaluateur(_r));
        Publier("speedrun.attempt.opened", new { sessionId = SessionId, attemptId = AttemptId, attemptNumber = Numero, cause = "restart" });
    }

    /// <summary>La borne n'a pas pu replacer le jeu (RetroArch injoignable) : la relance est refusee, le jeu reste ou il est.</summary>
    public void RelanceEchouee(string raison)
    {
        if (!_relance) return;
        _relance = false;
        Ouvrir(new Evaluateur(_r));
        Publier("speedrun.restart.failed", new { sessionId = SessionId, attemptId = AttemptId, reason = raison });
    }

    public IReadOnlyList<ActionPermise> ActionsPermises()
    {
        var enCourse = _courante.Etat is EtatTentative.EnCourse or EtatTentative.ArriveeProvisoire;
        var liste = new List<ActionPermise>
        {
            _relance
                ? new ActionPermise(ActionRelancer, false, "restart_in_progress")
                : new ActionPermise(ActionRelancer, true, null, Confirmation: enCourse),
            !_relance && enCourse
                ? new ActionPermise(ActionAbandonner, true, null, Confirmation: true)
                : new ActionPermise(ActionAbandonner, false, _relance ? "restart_in_progress" : "no_attempt_running"),
            new(ActionComparaison, true, null),
        };
        liste.AddRange(ActionsDEntrainement.Select(a => new ActionPermise(a, false, "ranked_challenge")));
        return liste;
    }

    public InstantaneSpeedrun Instantane()
    {
        var res = _courante.Resultat();
        var officielle = Officielle(res);
        DepartPublie? depart = officielle?.Depart is { } f ? new DepartPublie(f, _horlogeDuDepart, _r.Cadence) : null;
        var fini = res.Etat == EtatTentative.Finie;
        ChronoPublie? officiel = fini && res.FramesOfficielles is { } frames
            ? Chrono(frames, false)
            : depart is not null && FrameDArriveeProvisoire(res) is { } arrivee ? Chrono(arrivee - depart.Frame, true) : null;
        return new InstantaneSpeedrun(
            SessionId, AttemptId, Numero, DefiId, _r.Version, Etat, _relance ? null : res.Cause, Sequence, depart, officiel,
            [], new ComparaisonsPubliees(_comparaison, MeilleurTemps is { } pb ? Chrono(pb, false) : null),
            ActionsPermises(), res.Etat is EtatTentative.Finie or EtatTentative.Ratee or EtatTentative.Annulee or EtatTentative.Abandonnee
                ? Demonstration(res) : null, res.Compteurs);
    }

    public DemonstrationPubliee Demonstration(ResultatTentative res)
        => new(res.Cale?.Frames is { } c ? Chrono(c, false) : null, res.Independant?.Frames is { } i ? Chrono(i, false) : null,
            res.TempsDuJeuSecondes, res.ControleTempsDuJeu);

    private ChronoPublie Chrono(long frames, bool provisoire) => new(frames, Chronometrage.Afficher(frames, _r.Cadence), provisoire);

    private static MesureChrono? Officielle(ResultatTentative res)
        => res.ChronoOfficiel == Reglement.ChronoIndependant ? res.Independant : res.Cale;

    private static long? FrameDArriveeProvisoire(ResultatTentative res)
        => res.Etat == EtatTentative.ArriveeProvisoire ? res.Journal.LastOrDefault(c => c.Role == "finish")?.Frame : null;

    private void Promouvoir(Evaluateur e)
    {
        Ouvrir(e);
        Publier("speedrun.attempt.opened", new { sessionId = SessionId, attemptId = AttemptId, attemptNumber = Numero, cause = "start" });
    }

    private void Ouvrir(Evaluateur e)
    {
        _courante = e;
        _etatVu = EtatTentative.Preparee;
        _horlogeDuDepart = null;
        AttemptId = _nouvelId();
        Numero++;
    }

    /// <summary>Compare l'etat de la tentative courante a celui deja publie, et publie ce qui a change.</summary>
    /// <summary>
    /// LE DEPART EMPECHE PAR UN CODE DE TRICHE DU JEU (mode debug de Sonic, decision du user du 31/10) : une condition de
    /// depart porte sur une adresse que le reglement annule aussi par une ligne LEVEL_CHEAT_*, et sa valeur connue la rend
    /// fausse. Le joueur l'apprend une fois, puis de nouveau s'il revient au titre et recommence avec le code.
    /// </summary>
    private void SignalerLaTriche()
    {
        if (_courante.Etat != EtatTentative.Preparee) return;
        var adresses = _r.AnnulationsJeu.Where(e => e.Action?.StartsWith("LEVEL_CHEAT", StringComparison.Ordinal) == true && e.Adresse is not null)
            .Select(e => e.Adresse!.Value).ToHashSet();
        var bloque = _r.Depart.Any(c => adresses.Contains(c.Adresse) && _courante.ValeurConnue(c.Adresse) is { } v && !c.Vraie(v));
        if (bloque && !_tricheSignalee)
        {
            _messages.Add(new MessageJoueur("speedrun.msg.debug_blocked", "Debug mode active: challenge unavailable, go back to the title screen."));
        }

        _tricheSignalee = bloque;
    }

    private void Constater()
    {
        SignalerLaTriche();
        var etat = _courante.Etat;
        if (etat == _etatVu) return;
        var avant = _etatVu;
        _etatVu = etat;
        var res = _courante.Resultat();

        var partie = avant is EtatTentative.EnCourse or EtatTentative.ArriveeProvisoire;
        if (!partie && etat is EtatTentative.EnCourse or EtatTentative.ArriveeProvisoire or EtatTentative.Finie)
        {
            _horlogeDuDepart = _horlogeDeLImage;
            _horlogeDArrivee = null;
            var depart = Officielle(res)?.Depart;
            Publier("speedrun.started", new { sessionId = SessionId, attemptId = AttemptId, frame = depart, clockMs = _horlogeDuDepart, rate = _r.Cadence });
            partie = true;
            // « started » dit deja RUNNING : pas de « speedrun.state » en double.
            if (etat == EtatTentative.EnCourse) return;
        }

        if (etat == EtatTentative.ArriveeProvisoire)
        {
            _horlogeDArrivee = _horlogeDeLImage;
            Publier("speedrun.finish.provisional", new { sessionId = SessionId, attemptId = AttemptId, frame = FrameDArriveeProvisoire(res) });
            return;
        }

        if (!_courante.EstTerminee)
        {
            if (etat == EtatTentative.Armee && !_departAnnonce)
            {
                _departAnnonce = true;
                _messages.Add(new MessageJoueur("speedrun.msg.ready",
                    "Challenge ready. Your best time is sent when you quit the game. Hotkey + Left: restart. Hotkey + Start: quit."));
            }

            Publier("speedrun.state", new { sessionId = SessionId, attemptId = AttemptId, state = Etat });
            return;
        }

        // Une tentative jamais partie (relance depuis l'ecran titre) n'est pas une tentative : ni journal, ni bilan.
        if (!partie && !res.Journal.Any(c => c.Role == "start"))
        {
            Publier("speedrun.state", new { sessionId = SessionId, attemptId = AttemptId, state = Etat });
            return;
        }

        _closes.Add((AttemptId, res));
        DerniereArrivee = EstClassable(res) && res.FramesOfficielles is { } arrivee ? (arrivee, res.Independant?.Frames) : null;
        var precedent = MeilleurTemps;
        if (EstClassable(res) && res.FramesOfficielles is { } frames && (precedent is not { } pb || frames < pb))
        {
            MeilleurTemps = frames;
            MeilleurIndependant = res.Independant?.Frames;
        }
        if (EstClassable(res) && res.FramesOfficielles is { } temps && (_meilleure?.Resultat.FramesOfficielles is not { } vu || temps < vu)
            && _horlogeDuDepart is { } departMs && (_horlogeDArrivee ?? _horlogeDeLImage) is { } arriveeMs)
            _meilleure = new TentativeFinie(AttemptId, Numero, res, departMs, arriveeMs);
        _relanceAutomatique = DelaiDeRelance(_r, res.Etat, res.Cause);
        if (res.Etat == EtatTentative.Ratee && _relanceAutomatique is not null)
            _messages.Add(new MessageJoueur("speedrun.msg.failed_restart", "Attempt failed: back to the start."));
        else if (MessageDeFin(res, precedent) is { } message) _messages.Add(message);
        if (AvertissementDeRalenti(_r, res, _horlogeDuDepart, _horlogeDArrivee ?? _horlogeDeLImage) is { } lent) _messages.Add(lent);
        Publier("speedrun.attempt.ended", new
        {
            sessionId = SessionId,
            attemptId = AttemptId,
            state = EtatHud(etat),
            cause = res.Cause,
            frame = res.FrameDeFin,
            official = res.FramesOfficielles is { } f ? Chrono(f, false) : null,
            demo = Demonstration(res),
        });
    }

    /// <summary>Un temps fini que le temps du jeu ne contredit pas : lui seul compte comme meilleur temps et part au site.</summary>
    public static bool EstClassable(ResultatTentative r) => r.Etat == EtatTentative.Finie && r.ControleTempsDuJeu != "ecart";

    /// <summary>
    /// CE QUE LE JOUEUR APPREND A LA FIN D'UNE TENTATIVE (decision du user du 30/10) : le classement ne vient qu'a la
    /// fermeture du jeu, seul moment ou le bilan du wrapper existe. Une tentative abandonnee ne dit rien : le joueur
    /// vient de la quitter.
    /// </summary>
    private static MessageJoueur? MessageDeFin(ResultatTentative r, long? precedent)
    {
        const string Relancer = "Hotkey + Left: restart.";
        if (r.Etat == EtatTentative.Ratee) return new MessageJoueur("speedrun.msg.failed", "Attempt failed. " + Relancer);
        if (r.Etat == EtatTentative.Annulee)
        {
            return r.Cause switch
            {
                "pause" or "PAUSE_ON" => new MessageJoueur("speedrun.msg.voided_pause", "Attempt voided: pause. " + Relancer),
                "fast_forward" => new MessageJoueur("speedrun.msg.voided_fast_forward", "Attempt voided: fast forward. " + Relancer),
                "slowdown" => new MessageJoueur("speedrun.msg.voided_slowdown", "Attempt voided: the game slowed down (display at 50 Hz? set 60 Hz, PAL60 in RGB). " + Relancer),
                var c when c?.StartsWith("LEVEL_CHEAT", StringComparison.Ordinal) == true
                    => new MessageJoueur("speedrun.msg.voided_debug", "Attempt voided: debug mode. Go back to the title screen."),
                _ => new MessageJoueur("speedrun.msg.voided", "Attempt voided. " + Relancer),
            };
        }

        if (r.Etat != EtatTentative.Finie || r.FramesOfficielles is not { } frames) return null;
        var temps = ("time", Chronometrage.Afficher(frames, r.Cadence));
        if (!EstClassable(r))
            return new MessageJoueur("speedrun.msg.finish_unranked", "Finish {time}: the game timer disagrees, this time will not be ranked.", temps);
        if (precedent is { } pb && frames >= pb)
            return new MessageJoueur("speedrun.msg.finish", "Finish {time} (best {best}). Ranking when you quit the game.",
                temps, ("best", Chronometrage.Afficher(pb, r.Cadence)));
        return new MessageJoueur("speedrun.msg.finish_best", "Finish {time}: new best time. Ranking when you quit the game.", temps);
    }

    /// <summary>Au-dessus, la vitesse du jeu ne merite pas d'avertissement (le grain de l'horloge).</summary>
    public const double VitesseSansAvertissement = 0.99;

    /// <summary>
    /// UN JEU QUI TOURNE LENTEMENT (decision du user du 07/11) : sous 97 %, la tentative est annulee ; entre 97 et 99 %, le
    /// temps compte (le site tolere 3 %), mais le joueur est prevenu. Un ecran a 59 Hz ralentit le jeu de 1,5 % : chaque
    /// image lui coute du temps reel, et un niveau long peut finir au-dela de la tolerance. Pur.
    /// </summary>
    public static MessageJoueur? AvertissementDeRalenti(Reglement r, ResultatTentative res, long? departMs, long? arriveeMs)
    {
        if (!r.Kind.StartsWith("speedrun", StringComparison.Ordinal) || !EstClassable(res)) return null;
        var chrono = res.ChronoOfficiel == Reglement.ChronoIndependant ? res.Independant : res.Cale;
        if (chrono?.Depart is not { } d || chrono.Arrivee is not { } a
            || Evaluateur.Vitesse(a - d, departMs, arriveeMs, res.Cadence) is not { } vitesse || vitesse >= VitesseSansAvertissement)
            return null;
        var pourcent = ((1 - vitesse) * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return new MessageJoueur("speedrun.msg.slow_game",
            "Warning: the game ran {percent}% slow. Set your screen to 60 Hz (a 59 Hz screen slows the game down).",
            ("percent", pourcent));
    }

    private void Publier(string type, object donnees) => _aPublier.Add(new EvenementSession(type, ++Sequence, donnees));
}
