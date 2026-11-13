using System.Diagnostics;
using System.Globalization;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LA PROGRESSION D'UN LANCEMENT (demande du user du 13/11) : les etapes reelles de la preparation d'un jeu, dans l'ordre, et
/// ce que chacune fait. Un seul modele pour tous les ecrans de lancement et d'intro de l'API (speedrun, challenge, World
/// Scoring, MULTIPLAYER, MASS RUN, boite d'attente) ; la barre discrete du bas (<see cref="BarreDeLancement"/>) le dessine.
/// <list type="bullet">
/// <item>PROGRESSION HONNETE : la barre avance d'une etape quand cette etape finit vraiment (<see cref="Finir(string)"/>), jamais
/// au temps qui passe. Des etapes paralleles (fantomes, Deck, manettes) avancent chacune a sa fin.</item>
/// <item>RIEN N'ATTEND LA BARRE : marquer une etape pose un etat sous un verrou, sans rien dessiner ni attendre ; l'ecran le
/// relit a chaque image qu'il dessine de toute facon.</item>
/// <item>EN ANGLAIS quelle que soit la langue de la borne (decision du user du 13/11) : les textes sont ecrits ici, ils ne
/// passent pas par les traductions.</item>
/// </list>
/// Une etape inconnue est ignoree : un appelant qui marque une etape que cet ecran n'a pas ne casse rien.
/// </summary>
public sealed class ProgressionDuLancement
{
    public enum EtatDEtape
    {
        Attente,
        EnCours,
        Faite,
        Echouee,
    }

    /// <summary>Une etape : son identifiant, son texte (anglais, « Preparing ghosts… »), son etat et sa duree mesuree.</summary>
    public sealed record Etape(string Id, string Texte, EtatDEtape Etat, double? DureeMs);

    /// <summary>
    /// Ce que la barre montre a un instant : le texte de l'etape en cours, la part faite (etapes finies sur le total),
    /// <c>Finie</c> quand toutes le sont, <c>Echec</c> quand l'une a echoue.
    /// </summary>
    public sealed record Vue(string Texte, int Faites, int Total, bool Finie, bool Echec)
    {
        public double Part => Total == 0 ? 1 : (double)Faites / Total;
    }

    /// <summary>Le texte quand tout est fait, le temps que l'ecran dise « YOUR TURN! » et s'efface.</summary>
    public const string TexteFini = "Ready";

    /// <summary>Le texte d'un lancement arrete par un echec, quand plus rien ne tourne (l'ecran s'efface le plus souvent aussitot).</summary>
    public const string TexteEchec = "Launch failed";

    private readonly object _verrou = new();
    private readonly List<(string Id, string Texte)> _etapes;
    private readonly Dictionary<string, EtatDEtape> _etats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _debuts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _durees = new(StringComparer.Ordinal);
    private readonly Stopwatch _horloge = Stopwatch.StartNew();

    public ProgressionDuLancement(string cle, IEnumerable<(string Id, string Texte)> etapes)
    {
        Cle = cle;
        _etapes = etapes.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        foreach (var (id, _) in _etapes) _etats[id] = EtatDEtape.Attente;
    }

    /// <summary>Le lancement suivi (l'epreuve, la salle, la gameroom) : il sert au journal.</summary>
    public string Cle { get; }

    /// <summary>Les identifiants des etapes, dans l'ordre.</summary>
    public IReadOnlyList<string> Ids
    {
        get { lock (_verrou) return _etapes.Select(e => e.Id).ToList(); }
    }

    /// <summary>L'etape commence (une etape deja commencee ou finie ne bouge pas).</summary>
    public void Commencer(string id)
    {
        lock (_verrou)
        {
            if (!_etats.TryGetValue(id, out var etat) || etat != EtatDEtape.Attente) return;
            _etats[id] = EtatDEtape.EnCours;
            _debuts[id] = _horloge.ElapsedTicks;
        }
    }

    /// <summary>L'etape est faite : la barre avance d'un cran. Une etape jamais commencee est faite aussitot (duree nulle).</summary>
    public void Finir(string id) => Clore(id, EtatDEtape.Faite);

    /// <summary>L'etape a echoue : elle compte comme passee, la barre le dit (le lancement s'arrete le plus souvent).</summary>
    public void Echouer(string id) => Clore(id, EtatDEtape.Echouee);

    /// <summary>
    /// L'etape n'aura pas lieu sur ce chemin (les manettes d'un defi qui part par ES : ES les calcule lui-meme) : elle sort du
    /// compte. Seule une etape a venir sort ; la part ne peut qu'augmenter.
    /// </summary>
    public void Retirer(string id)
    {
        lock (_verrou)
        {
            if (!_etats.TryGetValue(id, out var etat) || etat != EtatDEtape.Attente) return;
            _etapes.RemoveAll(e => e.Id == id);
            _etats.Remove(id);
        }
    }

    /// <summary>Plusieurs etapes faites d'un coup (des controles deja passes quand l'ecran parait).</summary>
    public void Finir(params string[] ids)
    {
        foreach (var id in ids) Finir(id);
    }

    /// <summary>L'etape le temps de <paramref name="travail"/> : commencee, faite a son retour, echouee s'il leve (l'exception ressort).</summary>
    public T Mesurer<T>(string id, Func<T> travail)
    {
        Commencer(id);
        try
        {
            var resultat = travail();
            Finir(id);
            return resultat;
        }
        catch
        {
            Echouer(id);
            throw;
        }
    }

    /// <summary>Comme <see cref="Mesurer{T}"/>, pour un travail asynchrone.</summary>
    public async Task<T> MesurerAsync<T>(string id, Func<Task<T>> travail)
    {
        Commencer(id);
        try
        {
            var resultat = await travail().ConfigureAwait(false);
            Finir(id);
            return resultat;
        }
        catch
        {
            Echouer(id);
            throw;
        }
    }

    /// <summary>Comme <see cref="MesurerAsync{T}"/>, sans resultat.</summary>
    public async Task MesurerAsync(string id, Func<Task> travail)
    {
        Commencer(id);
        try
        {
            await travail().ConfigureAwait(false);
            Finir(id);
        }
        catch
        {
            Echouer(id);
            throw;
        }
    }

    private void Clore(string id, EtatDEtape fin)
    {
        lock (_verrou)
        {
            if (!_etats.TryGetValue(id, out var etat) || etat is EtatDEtape.Faite or EtatDEtape.Echouee) return;
            _etats[id] = fin;
            _durees[id] = _debuts.TryGetValue(id, out var debut) ? (_horloge.ElapsedTicks - debut) * 1000d / Stopwatch.Frequency : 0;
        }
    }

    /// <summary>
    /// Ce que la barre montre : le texte de la premiere etape en cours dans l'ordre (des etapes paralleles se disent l'une
    /// apres l'autre, a mesure qu'elles finissent), sinon de la prochaine a venir ; la part des etapes passees.
    /// </summary>
    public Vue Lire()
    {
        lock (_verrou)
        {
            var faites = _etapes.Count(e => _etats[e.Id] is EtatDEtape.Faite or EtatDEtape.Echouee);
            var echec = _etapes.Any(e => _etats[e.Id] == EtatDEtape.Echouee);
            var texte = _etapes.FirstOrDefault(e => _etats[e.Id] == EtatDEtape.EnCours).Texte
                ?? (echec ? TexteEchec : null)
                ?? _etapes.FirstOrDefault(e => _etats[e.Id] == EtatDEtape.Attente).Texte
                ?? TexteFini;
            return new Vue(texte, faites, _etapes.Count, faites == _etapes.Count, echec);
        }
    }

    /// <summary>Les etapes, leur etat et leur duree (pour le journal et les tests).</summary>
    public IReadOnlyList<Etape> Etapes()
    {
        lock (_verrou)
        {
            return _etapes.Select(e => new Etape(e.Id, e.Texte, _etats[e.Id], _durees.TryGetValue(e.Id, out var d) ? d : null)).ToList();
        }
    }

    /// <summary>« ghosts 820 ms, deck 1 904 ms, controllers en cours » : la ligne du journal, utile a la prochaine mesure de lenteur.</summary>
    public string Resume()
        => string.Join(", ", Etapes().Select(e => e.Id + " " + e.Etat switch
        {
            EtatDEtape.Faite => (e.DureeMs ?? 0).ToString("0", CultureInfo.InvariantCulture) + " ms",
            EtatDEtape.Echouee => "echec apres " + (e.DureeMs ?? 0).ToString("0", CultureInfo.InvariantCulture) + " ms",
            EtatDEtape.EnCours => "en cours",
            _ => "a venir",
        }));

    // ── Les etapes de chaque ecran (textes en anglais, decision du user du 13/11) ──

    public const string Regles = "rules";
    public const string Fantomes = "ghosts";
    public const string Deck = "deck";
    public const string Manettes = "controllers";
    public const string Lanceur = "launcher";
    public const string PremiereImage = "frame";
    public const string Menu = "menu";
    public const string Coeur = "core";
    public const string Direct = "live";
    public const string Jeu = "game";
    public const string Place = "spot";
    public const string Course = "race";
    public const string SalleDAttente = "waiting-room";
    public const string Paquet = "unpack";
    public const string Relais = "relay";
    public const string Panneaux = "panels";
    public const string Session = "session";
    public const string Joueurs = "players";
    public const string Hote = "host";

    /// <summary>Speedrun et challenge du panneau des defis : les controles passes, puis la partie lente, puis la premiere image.</summary>
    public static ProgressionDuLancement Epreuve(string cle, bool departSurEtat) => new(cle,
    [
        (Regles, "Checking the rules…"),
        (Fantomes, "Preparing ghosts…"),
        (Deck, "Loading Player Deck…"),
        (Manettes, "Reading controllers…"),
        (Lanceur, "Starting game…"),
        // Un depart sur etat : le jeu est la quand l'etat est pose, pret a charger ; sinon a sa premiere image.
        (PremiereImage, departSurEtat ? "Loading the start state…" : "Waiting for the first frame…"),
    ]);

    /// <summary>
    /// Le defi World Scoring (CHALLENGE du panneau de classement). « Going live… » se retire d'un defi prive, « Reading
    /// controllers… » d'un defi qui part par ES (ES calcule ses manettes lui-meme) : les deux se savent juste apres l'appui.
    /// </summary>
    public static ProgressionDuLancement WorldScoring(string cle) => new(cle,
    [
        (Menu, "Closing the menu…"),
        (Coeur, "Setting up the core…"),
        (Deck, "Loading Player Deck…"),
        (Direct, "Going live…"),
        (Manettes, "Reading controllers…"),
        (Lanceur, "Starting game…"),
        (Jeu, "Waiting for the game…"),
    ]);

    /// <summary>Une course MASS RUN rejointe depuis le panneau.</summary>
    public static ProgressionDuLancement MassRun(string cle) => new(cle,
    [
        (Place, "Joining the room…"),
        (Course, "Preparing the race…"),
        (Manettes, "Reading controllers…"),
        (Lanceur, "Starting game…"),
        (SalleDAttente, "Reaching the waiting room…"),
    ]);

    /// <summary>
    /// Une rencontre MULTIPLAYER. L'hote prepare, lance, puis ouvre la session et attend ses invites ; l'invite attend la
    /// session de l'hote, prepare, lance, puis le rejoint.
    /// </summary>
    public static ProgressionDuLancement Multiplayer(string cle, bool hote) => new(cle, hote
        ? [
            (Paquet, "Unpacking the game…"),
            (Relais, "Choosing a relay…"),
            (Manettes, "Reading controllers…"),
            (Panneaux, "Drawing player panels…"),
            (Lanceur, "Starting game…"),
            (Jeu, "Waiting for the game…"),
            (Session, "Opening the session…"),
            (Joueurs, "Waiting for players…"),
        ]
        : [
            (Session, "Waiting for the host…"),
            (Paquet, "Unpacking the game…"),
            (Manettes, "Reading controllers…"),
            (Panneaux, "Drawing player panels…"),
            (Lanceur, "Starting game…"),
            (Jeu, "Waiting for the game…"),
            (Hote, "Joining the host…"),
        ]);

    public const string Spectateur = "viewer";
    public const string Replay = "replay";

    /// <summary>
    /// Regarder un replay depuis le panneau de classement : la seance du spectateur, la preparation du replay (manifeste, objet,
    /// verification, ROM), puis le jeu. Un replay a recopier d'abord (« replicating ») reste en preparation jusqu'au jeu.
    /// </summary>
    public static ProgressionDuLancement RegarderUnReplay(string cle) => new(cle,
    [
        (Spectateur, "Opening the viewer session…"),
        (Replay, "Preparing the replay…"),
        (Jeu, "Starting game…"),
    ]);

    /// <summary>Rejoindre un direct (ou le regarder) depuis le panneau de classement.</summary>
    public static ProgressionDuLancement RejoindreUnDirect(string cle) => new(cle,
    [
        (Menu, "Closing the menu…"),
        (Session, "Joining the session…"),
        (Jeu, "Waiting for the game…"),
    ]);
}
