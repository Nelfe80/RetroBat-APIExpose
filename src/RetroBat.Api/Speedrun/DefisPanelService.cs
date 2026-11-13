using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;
using static RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Speedrun;

/// <summary>Ce qu'un type de RECORDS montre pour une epreuve. <c>Epinglee</c> : la ligne du joueur, s'il est classe.</summary>
public sealed record VueDeRecords(string Bandeau, IReadOnlyList<string> Colonnes, IReadOnlyList<LigneDeDefi> Lignes, int? Epinglee);

/// <summary>Un type de RECORDS (GLOBAL, un jeu complet, NIVEAUX...) et ses vues : plusieurs quand LB et RB ont une epreuve a choisir.</summary>
public sealed record TypeDeRecords(string Etiquette, IReadOnlyList<VueDeRecords> Vues);

/// <summary>
/// LE PANNEAU DES DEFIS (lot P5 du CDC du panneau) : l'appui long sur un jeu de la collection des defis l'ouvre a gauche du
/// menu d'ES, comme le classement et MULTIPLAYER. Il lit les defis du jeu dans la collection, le bilan des tentatives dans le
/// journal local, et lance une epreuve par la session de speedrun apres avoir referme le menu d'ES.
///
/// Les onglets sont dynamiques : une vue vide n'existe pas. RECORDS parait avec le premier temps publie sur le jeu, et TRAINING
/// attend le chargement des etats chez le joueur.
/// </summary>
public sealed partial class DefisPanelService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly DefisCollectionSyncService _collection;
    private readonly SpeedrunSessionService _session;
    private readonly LeaderboardOverlayService _overlay;
    private readonly EsControllerService _es;
    private readonly MediaRuntimeState? _etatDesMedias;
    private readonly InterfaceTextService? _textes;
    private readonly EmulationStationSettingsService? _reglages;
    private readonly SonDEmulationStation? _sonDEs;
    private readonly ILogger<DefisPanelService>? _journal;
    private readonly ApiContext? _contexte;
    private readonly ClassementsDesDefis? _classements;
    private readonly DefisPanelModel _modele = new();
    private readonly RetroBat.Api.MassRun.MassRunService? _massrun;
    private IReadOnlyList<RetroBat.Api.MassRun.MassRunService.SalleOuverte> _salles = [];
    private System.Threading.Timer? _minuteurMassRun;
    private readonly FantomesDuSpeedrun? _fantomes;
    /// <summary>(12/11) Le selecteur des fantomes (X) : leur choix, l'opacite, les pseudos.</summary>
    private readonly SelecteurDeReglages _selecteur = new();
    private int _tics;
    private readonly object _verrou = new();

    private string _chemin = "";
    private IReadOnlyList<Reglement> _defis = [];
    private Dictionary<DefisPanelModel.Onglet, List<(LigneDeDefi Ligne, Reglement? Defi)>> _lignes = new();
    private IReadOnlyDictionary<string, (int Tentatives, long? Meilleur)> _bilan = new Dictionary<string, (int, long?)>();
    /// <summary>Le chemin de l'image de chaque challenge du jeu, resolu une fois a l'ouverture.</summary>
    private IReadOnlyDictionary<string, string> _images = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, ClassementDEpreuve> _tableaux = new Dictionary<string, ClassementDEpreuve>();
    private IReadOnlyList<TypeDeRecords> _records = [];
    private string _glypheValider = "a";
    private string _glypheDefier = "y";
    private string _glypheAnnuler = "b";
    private string _glypheX = "x";
    private string _glypheLb = "l";
    private string _glypheRb = "r";
    private string _message = "";
    private bool _lancement;

    public DefisPanelService(
        IOptionsMonitor<ApiExposeOptions> options,
        DefisCollectionSyncService collection,
        SpeedrunSessionService session,
        LeaderboardOverlayService overlay,
        EsControllerService es,
        MediaRuntimeState? etatDesMedias = null,
        InterfaceTextService? textes = null,
        EmulationStationSettingsService? reglages = null,
        SonDEmulationStation? sonDEs = null,
        ILogger<DefisPanelService>? journal = null,
        ApiContext? contexte = null,
        ClassementsDesDefis? classements = null,
        RetroBat.Domain.Interfaces.IEventBus? bus = null,
        RetroBat.Api.MassRun.MassRunService? massrun = null,
        FantomesDuSpeedrun? fantomes = null)
    {
        _massrun = massrun;
        _fantomes = fantomes;
        _options = options;
        _collection = collection;
        _session = session;
        _overlay = overlay;
        _es = es;
        _etatDesMedias = etatDesMedias;
        _textes = textes;
        _reglages = reglages;
        _sonDEs = sonDEs;
        _journal = journal;
        _contexte = contexte;
        _classements = classements;
        _bus = bus;
        _abonnement = bus?.Subscribe<RetroBat.Domain.Events.EventEnvelope>(SurEvenement);
        if (_massrun is not null) _massrun.SalleQuittee += SurSalleQuittee;
    }

    public bool Actif => _options.CurrentValue.NelfePlay is { Enabled: true, ShowChallengesCollection: true };

    /// <summary>Le carrousel montre-t-il la collection des defis ? C'est lui qui decide, pas le systeme du jeu.</summary>
    public bool DansLaCollection
        => string.Equals(_etatDesMedias?.CarouselSystemId, DefisCollectionSyncService.CollectionName, StringComparison.OrdinalIgnoreCase);

    public bool Ouvert
    {
        get { lock (_verrou) return _modele.Etat != DefisPanelModel.Foyer.Ferme; }
    }

    /// <summary>A l'ecran et ES a la main : un « gauche » y entrerait.</summary>
    public bool AttendQuOnEntre
    {
        get { lock (_verrou) return _modele.Etat == DefisPanelModel.Foyer.MenuEs; }
    }

    public string CheminAffiche
    {
        get { lock (_verrou) return _chemin; }
    }

    public void Ouvrir(string chemin, string glypheValider, string glypheAnnuler, string glypheX = "", string glypheLb = "", string glypheRb = "",
        string glypheDefier = "")
    {
        IReadOnlyList<string> regles;
        lock (_verrou)
        {
            _chemin = chemin;
            _nomDuJeu = _contexte?.Ui.Selected is { } choisi && CheminsEgaux(choisi.GamePath, chemin) && choisi.GameName.Length > 0
                ? choisi.GameName
                : NomDuFichier(chemin);
            _defis = _collection.DefisDuJeu(chemin);
            _images = ImagesDesDefis.Pour(_defis, chemin);
            _bilan = _session.BilanDuJournal();
            _glypheValider = glypheValider.Length > 0 ? glypheValider : "a";
            _glypheDefier = glypheDefier.Length > 0 ? glypheDefier : "y";
            _glypheAnnuler = glypheAnnuler.Length > 0 ? glypheAnnuler : "b";
            _glypheX = glypheX.Length > 0 ? glypheX : "x";
            _glypheLb = glypheLb.Length > 0 ? glypheLb : "l";
            _glypheRb = glypheRb.Length > 0 ? glypheRb : "r";
            _message = "";
            _lancement = false;
            _selecteur.Fermer();
            regles = _defis.Select(d => d.Ruleset).ToList();
            _tableaux = _classements?.Connus(regles) ?? new Dictionary<string, ClassementDEpreuve>();
            _modele.Ouvrir(Recomposer());
            PoserLesCompteurs();
        }

        _journal?.LogInformation("Defis : panneau ouvert sur {Chemin}, {Nombre} defi(s).", chemin, _defis.Count);
        _overlay.OuvrirDefis(EsMenuStyle.Lire(_journal), Contenu());
        DemarrerMassRun();
        if (_classements is not null && regles.Count > 0) _ = ActualiserLesClassementsAsync(_classements, chemin, regles);
    }

    /// <summary>Le frais arrive apres l'ouverture : le panneau se recompose s'il montre toujours ce jeu, le joueur reste ou il est.</summary>
    private async Task ActualiserLesClassementsAsync(ClassementsDesDefis classements, string chemin, IReadOnlyList<string> regles)
    {
        var tableaux = await classements.ChargerAsync(regles, CancellationToken.None).ConfigureAwait(false);
        lock (_verrou)
        {
            if (_modele.Etat == DefisPanelModel.Foyer.Ferme || _chemin != chemin) return;
            _tableaux = tableaux;
            _modele.PoserLesOnglets(Recomposer());
            PoserLesCompteurs();
        }

        Rafraichir();
    }

    private string Pseudo => _contexte?.PlayerPseudo ?? "";

    /// <summary>Les lignes et les records d'apres ce qu'on sait ; rend les onglets qui ont quelque chose a montrer.</summary>
    private List<DefisPanelModel.Onglet> Recomposer()
    {
        var langue = Langue();
        _lignes = Composer(_defis, _bilan, langue, _tableaux, Pseudo, _images);
        _records = ComposerLesRecords(_defis, _tableaux, langue, Pseudo);
        var onglets = _lignes.Where(p => p.Value.Any(l => l.Defi is not null)).Select(p => p.Key).ToList();
        if (_records.Count > 0) onglets.Add(DefisPanelModel.Onglet.Records);
        if (SalleDuJeu() is not null) onglets.Add(DefisPanelModel.Onglet.MassRun);
        return onglets;
    }

    // ── MASS RUN (CDC MASS RUN, lot L5) ──

    /// <summary>
    /// La salle ouverte sur ce jeu : celle qui prend des inscriptions d'abord, sinon celle en salle d'attente, sinon la course
    /// en cours.
    /// </summary>
    private RetroBat.Api.MassRun.MassRunService.SalleOuverte? SalleDuJeu()
    {
        var jeux = _defis.Select(d => d.SystemId + "/" + d.RomGroup).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maintenant = DateTime.UtcNow;
        return _salles.Where(s => jeux.Contains(s.Jeu) && maintenant < s.GoUtc.AddSeconds(s.Limite))
            .OrderBy(s => s.Inscriptions(maintenant) ? 0 : s.GoUtc > maintenant ? 1 : 2).ThenBy(s => s.GoUtc).FirstOrDefault();
    }

    /// <summary>Le minuteur de l'onglet : le decompte chaque seconde, les salles du noeud toutes les 3 s.</summary>
    private void DemarrerMassRun()
    {
        if (_massrun is null) return;
        _tics = 0;
        _minuteurMassRun?.Dispose();
        _minuteurMassRun = new System.Threading.Timer(_ => _ = TicMassRunAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private async Task TicMassRunAsync()
    {
        if (_massrun is null) return;
        bool ouvert;
        lock (_verrou) ouvert = _modele.Etat != DefisPanelModel.Foyer.Ferme;
        if (!ouvert)
        {
            _minuteurMassRun?.Dispose();
            _minuteurMassRun = null;
            return;
        }
        if (_tics++ % 3 == 0)
        {
            var salles = await _massrun.LireLesSallesAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_verrou)
            {
                if (_modele.Etat == DefisPanelModel.Foyer.Ferme) return;
                _salles = salles;
                _modele.PoserLesOnglets(Recomposer());
                PoserLesCompteurs();
            }
            Rafraichir();
            return;
        }
        bool surLOnglet;
        lock (_verrou) surLOnglet = _modele.OngletCourant == DefisPanelModel.Onglet.MassRun;
        if (surLOnglet) Rafraichir();
    }

    /// <summary>
    /// L'onglet d'une salle, en trois temps (04/11) : les inscriptions, decomptees jusqu'a leur fin (la salle d'attente des
    /// derniers inscrits dure encore une minute dans le jeu, jusqu'au GO!) ; la salle d'attente, sans inscription ; la course.
    /// </summary>
    private ContenuDefis ContenuMassRun(RetroBat.Api.MassRun.MassRunService.SalleOuverte salle)
    {
        var maintenant = DateTime.UtcNow;
        var inscriptions = salle.Inscriptions(maintenant);
        var avant = salle.GoUtc > maintenant;
        var format = FormatDe(salle);
        var niveau = T("massrun.panel.level", "Level") + " " + RetroBat.Api.MassRun.JeuxMassRun.NomDuNiveau(salle.Niveau);
        var inscrits = salle.Inscrits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var vue = new SalleMassRun(
            inscriptions ? T("massrun.panel.open", "Room open") : avant ? T("massrun.panel.closed", "Registration closed") : T("massrun.panel.running", "Run in progress"),
            true,
            $"{format} · {_nomDuJeu} · {niveau}",
            avant ? T("massrun.panel.registered", "Runners registered") : T("massrun.panel.finished", "Finished"),
            avant ? inscrits : $"{salle.Arrives}/{salle.Inscrits}",
            inscriptions ? T("massrun.panel.closes_in", "Registration closes in") : avant ? T("massrun.panel.start_in", "Start in") : T("massrun.panel.race_time", "Run time"),
            Horloge(inscriptions ? salle.InscriptionsUtc - maintenant : avant ? salle.GoUtc - maintenant : maintenant - salle.GoUtc),
            $"{RegleDe(salle)} · " + T("massrun.panel.color", "you run in your avatar color"));
        var peutRejoindre = inscriptions;
        return new ContenuDefis(
            _modele.Onglets.Select(DefisPanelModel.Titre).ToList(),
            _modele.Onglets.ToList().IndexOf(DefisPanelModel.Onglet.MassRun),
            _modele.Etat == DefisPanelModel.Foyer.Panneau,
            false, [], [], -1, [],
            peutRejoindre ? new Aide(_glypheDefier, T("massrun.panel.join", "Join the run")) : null,
            Aides(),
            Pseudo: Pseudo,
            EnLigne: LiaisonNelfePlay.EnLigne,
            MassRun: vue);
    }

    /// <summary>
    /// Rejoindre la salle : le menu d'ES se referme (comme pour une epreuve), puis la borne prend sa place, lance le jeu et
    /// montre le depart.
    /// </summary>
    private async Task RejoindreLaSalleAsync()
    {
        RetroBat.Api.MassRun.MassRunService.SalleOuverte? salle;
        string jeu, chemin;
        lock (_verrou)
        {
            if (_lancement || _massrun is null) return;
            salle = SalleDuJeu();
            if (salle is null || !salle.Inscriptions(DateTime.UtcNow)) return;
            _lancement = true;
            jeu = _nomDuJeu;
            chemin = _chemin;
        }
        try
        {
            string? fanart = null;
            try { fanart = RetroBat.Api.Media.MediaDuGamelist.Resoudre(chemin, "fanart", "image"); }
            catch (Exception ex) { _journal?.LogDebug(ex, "MASS RUN : fanart introuvable pour {Chemin}.", chemin); }
            EmulatorForeground.FocusEmulationStation();
            await Task.Delay(150).ConfigureAwait(false);
            await _es.TapAsync(new EsControllerTapRequest { Input = "back" }, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            if (await RejoindreAsync(salle, jeu, fanart).ConfigureAwait(false) is { } refus)
            {
                lock (_verrou) _message = refus;
                Rafraichir();
            }
        }
        finally
        {
            lock (_verrou) _lancement = false;
        }
    }

    private void PoserLesCompteurs()
    {
        _modele.PoserLesLignes(DefisPanelModel.Onglet.MassRun, SalleDuJeu() is null ? 0 : 1);
        foreach (var (onglet, lignes) in _lignes) _modele.PoserLesLignes(onglet, Choisissables(lignes).Count, DebutsDeGroupe(lignes));
        _modele.PoserLesTypes(_records.Count);
        PoserLaVueDesRecords();
    }

    private TypeDeRecords? TypeCourant => _records.Count > 0 ? _records[Math.Min(_modele.Type, _records.Count - 1)] : null;

    private VueDeRecords? VueCourante => TypeCourant is { } type ? type.Vues[Math.Min(_modele.Epreuve, type.Vues.Count - 1)] : null;

    private void PoserLaVueDesRecords()
    {
        _modele.PoserLesEpreuves(TypeCourant?.Vues.Count ?? 0);
        _modele.PoserLesLignes(DefisPanelModel.Onglet.Records, VueCourante?.Lignes.Count ?? 0);
    }

    public void Fermer()
    {
        lock (_verrou)
        {
            _selecteur.Fermer();
            if (_modele.Etat == DefisPanelModel.Foyer.Ferme) return;
            _modele.Fermer();
        }

        _overlay.Fermer();
    }

    /// <summary>Un jeu demarre : la fenetre s'efface sans rendre le premier plan a ES, c'est l'emulateur qui le prend.</summary>
    public void FermerPourLeJeu()
    {
        lock (_verrou)
        {
            _selecteur.Fermer();
            if (_modele.Etat == DefisPanelModel.Foyer.Ferme) return;
            _modele.Fermer();
        }

        _overlay.FermerPourLeJeu();
    }

    /// <summary>Le premier plan est parti, ou plus rien n'arrive : la main revient a ES, le panneau reste.</summary>
    public void RendreLaMain()
    {
        lock (_verrou)
        {
            _selecteur.Fermer();
            if (_modele.Etat != DefisPanelModel.Foyer.Panneau) return;
            _modele.Annuler();
        }

        _overlay.RendreLeFocus();
        Rafraichir();
    }

    public void Direction(string? identite)
    {
        DefisPanelModel.Effet effet;
        lock (_verrou)
        {
            // (12/11) le selecteur ouvert garde les directions : haut et bas choisissent la ligne, gauche et droite la valeur ;
            // ni l'onglet ni le curseur des defis ne bougent
            if (SelecteurOuvert())
            {
                var reglages = ReglagesDesFantomes();
                var change = (identite ?? "").ToLowerInvariant() switch
                {
                    "left" => _selecteur.Gauche(reglages),
                    "right" => _selecteur.Droite(reglages),
                    "up" => _selecteur.Haut(reglages),
                    "down" => _selecteur.Bas(reglages),
                    _ => false,
                };
                effet = change ? DefisPanelModel.Effet.LigneChangee : DefisPanelModel.Effet.Rien;
            }
            else effet = (identite ?? "").ToLowerInvariant() switch
            {
                "left" => _modele.Gauche(),
                "right" => _modele.Droite(),
                "up" => _modele.Haut(),
                "down" => _modele.Bas(),
                _ => DefisPanelModel.Effet.Rien,
            };
        }

        Appliquer(effet);
    }

    public void GroupeSuivant()
    {
        DefisPanelModel.Effet effet;
        lock (_verrou) effet = SelecteurOuvert() ? DefisPanelModel.Effet.Rien : _modele.GroupeSuivant();
        Appliquer(effet);
    }

    public void GroupePrecedent()
    {
        DefisPanelModel.Effet effet;
        lock (_verrou) effet = SelecteurOuvert() ? DefisPanelModel.Effet.Rien : _modele.GroupePrecedent();
        Appliquer(effet);
    }

    public void X()
    {
        DefisPanelModel.Effet effet;
        lock (_verrou)
        {
            // (12/11, decision du user) X ouvre le selecteur des fantomes, et le referme
            if (SelecteurOuvert())
            {
                _selecteur.Fermer();
                effet = DefisPanelModel.Effet.LigneChangee;
            }
            else if (SurUnSpeedrunAFantomes())
            {
                _selecteur.Ouvrir(ReglagesDesFantomes());
                effet = DefisPanelModel.Effet.LigneChangee;
            }
            else
            {
                effet = _modele.X();
            }
        }

        Appliquer(effet);
    }

    /// <summary>Le curseur est sur un speedrun qui se court avec des fantomes : X fait defiler leur choix (D25).</summary>
    private bool SurUnSpeedrunAFantomes()
        => _fantomes is not null && _modele.Etat == DefisPanelModel.Foyer.Panneau && _modele.OngletCourant == DefisPanelModel.Onglet.Speedruns
           && DefiChoisi() is { } defi && FantomesDuSpeedrun.PourCeDefi(defi);

    /// <summary>
    /// (12/11) Le resume de la fiche : le choix, puis l'opacite et les pseudos quand ils ne sont pas ceux d'origine
    /// (« Les deux · 50 % · sans pseudos »). Sans fantome, le choix seul.
    /// </summary>
    private string LibelleDesFantomes()
    {
        var choix = _fantomes?.Choix ?? ChoixDesFantomes.MonRecordEtMondial;
        var suite = new List<string>();
        if (_fantomes is not null && choix != ChoixDesFantomes.Aucun)
        {
            if (_fantomes.OpaciteReduite) suite.Add(T("speedrun.panel.ghosts.opacity.half", "50%"));
            if (_fantomes.PseudosMasques) suite.Add(T("speedrun.panel.ghosts.summary.no_names", "no names"));
        }

        var nom = choix switch
        {
            ChoixDesFantomes.MonRecord => T("speedrun.panel.ghosts.mine", "My record"),
            ChoixDesFantomes.RecordMondial => T("speedrun.panel.ghosts.world", "World record"),
            ChoixDesFantomes.Aucun => T("speedrun.panel.ghosts.none", "None"),
            // la case de la fiche est une demi-largeur : le nom court quand la suite s'y ajoute
            _ => suite.Count > 0 ? T("speedrun.panel.ghosts.both.short", "Both") : T("speedrun.panel.ghosts.both", "My record + world record"),
        };
        return string.Join(" · ", [nom, .. suite]);
    }

    /// <summary>(12/11) Le selecteur est ouvert, sur un speedrun a fantomes ; sinon il se referme.</summary>
    private bool SelecteurOuvert()
    {
        if (!_selecteur.Ouvert) return false;
        if (SurUnSpeedrunAFantomes()) return true;
        _selecteur.Fermer();
        return false;
    }

    /// <summary>
    /// Les reglages du selecteur des fantomes, dans l'ordre des lignes : leur choix (celui de toujours, dans le meme ordre),
    /// l'opacite et les pseudos. Ces deux-la ne jouent pas sans fantome : grises, le curseur les saute.
    /// </summary>
    internal IReadOnlyList<ReglageDuSelecteur> ReglagesDesFantomes()
    {
        var f = _fantomes!;
        bool AvecFantomes() => f.Choix != ChoixDesFantomes.Aucun;
        return
        [
            new(T("speedrun.panel.ghosts", "Ghosts"),
                [T("speedrun.panel.ghosts.both", "My record + world record"), T("speedrun.panel.ghosts.mine", "My record"),
                 T("speedrun.panel.ghosts.world", "World record"), T("speedrun.panel.ghosts.none", "None")],
                () => (int)f.Choix, i => f.Choisir((ChoixDesFantomes)i)),
            new(T("speedrun.panel.ghosts.opacity", "Opacity"),
                [T("speedrun.panel.ghosts.opacity.normal", "Normal"), T("speedrun.panel.ghosts.opacity.half", "50%")],
                () => f.OpaciteReduite ? 1 : 0, i => f.ReduireLOpacite(i == 1), AvecFantomes),
            new(T("speedrun.panel.ghosts.names", "Names"),
                [T("speedrun.panel.ghosts.names.shown", "Shown"), T("speedrun.panel.ghosts.names.hidden", "Hidden, arrow at the edge")],
                () => f.PseudosMasques ? 1 : 0, i => f.MasquerLesPseudos(i == 1), AvecFantomes),
        ];
    }

    private SelecteurAffiche SelecteurAfficheDesFantomes()
    {
        var reglages = ReglagesDesFantomes();
        _selecteur.Recaler(reglages);
        return new SelecteurAffiche(T("speedrun.panel.ghosts.settings", "Ghost settings"),
            reglages.Select(r => new LigneDeReglage(r.Etiquette, r.Valeurs[Math.Clamp(r.Lire(), 0, r.Valeurs.Count - 1)], r.EstActif)).ToList(),
            _selecteur.Ligne);
    }

    /// <summary>
    /// Valider ne lance rien dans le panneau (essai du 01/11) : le menu d'ES se referme avant le lancement, et ES, qui
    /// reprend la main, entendait encore A et lancait le jeu une seconde fois. Deux Metal Slug 3 tournaient ensemble.
    /// </summary>
    public void Valider(bool appuye)
    {
        // (12/11) A referme le selecteur des fantomes et revient a la fiche, sans rien lancer
        if (!appuye) return;
        bool ferme;
        lock (_verrou)
        {
            ferme = SelecteurOuvert();
            if (ferme) _selecteur.Fermer();
        }

        if (ferme) Rafraichir();
    }

    /// <summary>LE BOUTON CHALLENGE (le « y » d'ES), comme au World Scoring : il lance l'epreuve choisie, a l'appui.</summary>
    public void Defier()
    {
        DefisPanelModel.Effet effet;
        lock (_verrou) effet = SelecteurOuvert() ? DefisPanelModel.Effet.Rien : _modele.Valider();
        Appliquer(effet);
    }

    /// <summary>« Retour » : le panneau rend la main a ES ; si ES l'avait deja, son menu se ferme et le panneau avec.</summary>
    public void Annuler(bool appuye)
    {
        if (!appuye) return;
        bool fermer;
        DefisPanelModel.Effet effet = DefisPanelModel.Effet.Rien;
        lock (_verrou)
        {
            // (12/11) B referme d'abord le selecteur des fantomes
            if (SelecteurOuvert())
            {
                _selecteur.Fermer();
                effet = DefisPanelModel.Effet.LigneChangee;
            }
        }

        if (effet != DefisPanelModel.Effet.Rien)
        {
            Appliquer(effet);
            return;
        }

        lock (_verrou)
        {
            fermer = _modele.Etat == DefisPanelModel.Foyer.MenuEs;
            if (!fermer) effet = _modele.Annuler();
        }

        if (fermer) Fermer();
        else Appliquer(effet);
    }

    private void Appliquer(DefisPanelModel.Effet effet)
    {
        switch (effet)
        {
            case DefisPanelModel.Effet.Rien:
                return;
            case DefisPanelModel.Effet.Entree:
                if (!_overlay.PrendreLeFocus())
                {
                    _journal?.LogInformation("Defis : le focus n'a pas pu etre pris, la main reste a ES.");
                    lock (_verrou) _modele.Annuler();
                }
                break;
            case DefisPanelModel.Effet.RendreLaMainAEs:
                _overlay.RendreLeFocus();
                break;
            case DefisPanelModel.Effet.TypeChange:
                lock (_verrou) PoserLaVueDesRecords();
                break;
            case DefisPanelModel.Effet.Valider:
                bool massrun;
                lock (_verrou) massrun = _modele.OngletCourant == DefisPanelModel.Onglet.MassRun;
                _ = massrun ? RejoindreLaSalleAsync() : LancerAsync();
                return;
        }

        Rafraichir();
    }

    /// <summary>
    /// LANCER L'EPREUVE CHOISIE : le menu d'ES est encore ouvert derriere le panneau. On le referme d'abord (ES au premier
    /// plan, puis « retour »), on coupe le son d'ES (le lancement direct ne le previent pas), puis la session lance le jeu
    /// avec le coeur du reglement et RetroAchievements coupe. Un refus se dit dans le panneau.
    /// </summary>
    private async Task LancerAsync()
    {
        Reglement? defi;
        string chemin;
        string jeu;
        lock (_verrou)
        {
            if (_lancement) return;
            defi = DefiChoisi();
            chemin = _chemin;
            jeu = _nomDuJeu;
            if (defi is null) return;
            _lancement = true;
        }

        try
        {
            EmulatorForeground.FocusEmulationStation();
            await Task.Delay(150).ConfigureAwait(false);
            await _es.TapAsync(new EsControllerTapRequest { Input = "back" }, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);

            if (LancerLEpreuve(defi, chemin, jeu) is { } raison)
            {
                lock (_verrou) _message = T("speedrun.panel.refused." + raison, "Cannot start: " + raison);
                Rafraichir();
            }
        }
        finally
        {
            lock (_verrou) _lancement = false;
        }
    }

    private Reglement? DefiChoisi()
    {
        if (!_lignes.TryGetValue(_modele.OngletCourant, out var lignes)) return null;
        var choisissables = Choisissables(lignes);
        return _modele.Ligne < choisissables.Count ? choisissables[_modele.Ligne].Defi : null;
    }

    private void Rafraichir() => _overlay.MontrerDefis(Contenu());

    private ContenuDefis Contenu()
    {
        lock (_verrou)
        {
            var onglet = _modele.OngletCourant;
            if (onglet == DefisPanelModel.Onglet.Records && VueCourante is { } vue) return ContenuDesRecords(vue);
            if (onglet == DefisPanelModel.Onglet.MassRun && SalleDuJeu() is { } salle) return ContenuMassRun(salle);
            var lignes = _lignes.GetValueOrDefault(onglet) ?? [];
            var choisissables = Choisissables(lignes);
            var courante = choisissables.Count == 0 ? -1 : lignes.IndexOf(choisissables[Math.Min(_modele.Ligne, choisissables.Count - 1)]);
            var defi = courante >= 0 ? lignes[courante].Defi : null;
            var bandeaux = onglet == DefisPanelModel.Onglet.Challenges;
            // (12/11) le selecteur ouvert ne lance rien : Y n'y fait rien, le bouton du bas s'efface
            var selecteur = SelecteurOuvert() ? SelecteurAfficheDesFantomes() : null;
            var action = onglet == DefisPanelModel.Onglet.Challenges
                ? T("speedrun.panel.launch_challenge", "Launch the challenge")
                : T("speedrun.panel.launch_speedrun", "Launch the speedrun");
            return new ContenuDefis(
                _modele.Onglets.Select(DefisPanelModel.Titre).ToList(),
                _modele.Onglets.ToList().IndexOf(onglet),
                _modele.Etat == DefisPanelModel.Foyer.Panneau,
                bandeaux,
                bandeaux ? [] : [T("speedrun.panel.record", "RECORD"), T("speedrun.panel.you", "YOU")],
                lignes.Select(l => l.Ligne).ToList(),
                courante,
                bandeaux || defi is null ? [] : Fiche(defi),
                defi is null || selecteur is not null ? null : new Aide(_glypheDefier, action),
                Aides(),
                Message: _message.Length > 0 ? _message : T("speedrun.panel.empty", "No open challenge for this game"),
                Pseudo: Pseudo,
                EnLigne: LiaisonNelfePlay.EnLigne,
                Selecteur: selecteur);
        }
    }

    /// <summary>RECORDS : le type choisi au bouton X, et l'epreuve choisie a LB et RB quand le type en a plusieurs.</summary>
    private ContenuDefis ContenuDesRecords(VueDeRecords vue) => new(
        _modele.Onglets.Select(DefisPanelModel.Titre).ToList(),
        _modele.Onglets.ToList().IndexOf(DefisPanelModel.Onglet.Records),
        _modele.Etat == DefisPanelModel.Foyer.Panneau,
        false,
        vue.Colonnes,
        vue.Lignes,
        Math.Min(_modele.Ligne, vue.Lignes.Count - 1),
        [],
        _records.Count > 1 ? new Aide(_glypheX, T("speedrun.panel.record_type", "Record type")) : null,
        Aides(),
        Bandeau: vue.Bandeau,
        Types: _records.Select(t => t.Etiquette).ToList(),
        TypeCourant: Math.Min(_modele.Type, _records.Count - 1),
        Pseudo: Pseudo,
        EnLigne: LiaisonNelfePlay.EnLigne,
        LigneEpinglee: vue.Epinglee);

    private IReadOnlyList<Aide> Aides()
    {
        if (_modele.Etat != DefisPanelModel.Foyer.Panneau)
            return [new Aide("dpad_left", T("speedrun.panel.help.open", "Open")), new Aide(_glypheAnnuler, T("speedrun.panel.help.close", "Close"))];
        if (SelecteurOuvert())
        {
            return
            [
                new Aide("dpad_updown", T("speedrun.panel.help.line", "Line")),
                new Aide("dpad_leftright", T("speedrun.panel.help.change", "Change")),
                new Aide(_glypheAnnuler, T("speedrun.panel.help.close", "Close")),
            ];
        }

        var aides = new List<Aide>
        {
            new("dpad_leftright", T("speedrun.panel.help.tab", "Tab")),
            new("dpad_updown", T("speedrun.panel.help.choose", "Choose")),
        };
        if (SurUnSpeedrunAFantomes()) aides.Add(new Aide(_glypheX, T("speedrun.panel.ghosts", "Ghosts")));
        if (_modele.OngletCourant == DefisPanelModel.Onglet.Records && _modele.Epreuves > 1)
        {
            aides.Add(new Aide(_glypheLb, T("speedrun.panel.help.previous", "Previous")));
            aides.Add(new Aide(_glypheRb, T("speedrun.panel.help.next", "Next")));
        }

        aides.Add(new Aide(_glypheAnnuler, T("speedrun.panel.help.back", "Back")));
        return aides;
    }

    private IReadOnlyList<ChampDeFiche> Fiche(Reglement defi, bool avecFantomes = true)
    {
        var depart = defi.StartPolicy == "normal_boot"
            ? T("speedrun.panel.start.normal", "Normal boot")
            : T("speedrun.panel.start.state", "Verified starting state");
        var interdits = new List<string>();
        if (defi.Echecs.Count > 0) interdits.Add(T("speedrun.panel.forbid.death", "dying"));
        if (defi.AnnulationsJeu.Any(e => e.Action == "PAUSE_ON") || defi.AnnulationsBorne.Contains("pause_clock_gap"))
            interdits.Add(T("speedrun.panel.forbid.pause", "pause"));
        IEnumerable<ChampDeFiche> fantomes = avecFantomes && _fantomes is not null && FantomesDuSpeedrun.PourCeDefi(defi)
            ? [new(T("speedrun.panel.ghosts", "Ghosts"), LibelleDesFantomes())]
            : [];
        return
        [
            .. fantomes,
            new(T("speedrun.panel.start", "Start"), depart),
            new(T("speedrun.panel.goal", "Goal"), Objectif(defi)),
            new(T("speedrun.panel.forbidden", "Forbidden"), LaPhrase(interdits)),
            new(T("speedrun.panel.record", "Record"), LeRecord(defi, _tableaux) is { } record
                ? Chronometrage.Afficher(record.Valeur, defi.Cadence) + " · " + record.Joueur : SansTemps),
            new(T("speedrun.panel.you", "You"), MaPlace(defi, _tableaux, Pseudo) is { } place
                ? Chronometrage.Afficher(place.Valeur, defi.Cadence) + " · #" + place.Rang
                : MonTemps(defi, _bilan, _tableaux, Pseudo)),
        ];
    }

    private const string SansTemps = "--:--.--";

    private static string LaPhrase(IReadOnlyList<string> mots)
    {
        var phrase = string.Join(", ", mots);
        return phrase.Length > 0 ? char.ToUpperInvariant(phrase[0]) + phrase[1..] : "-";
    }

    private static TempsClasse? LeRecord(Reglement defi, IReadOnlyDictionary<string, ClassementDEpreuve> tableaux)
        => tableaux.GetValueOrDefault(defi.Ruleset)?.Lignes.FirstOrDefault();

    private static TempsClasse? MaPlace(Reglement defi, IReadOnlyDictionary<string, ClassementDEpreuve> tableaux, string pseudo)
        => pseudo.Length == 0 ? null
            : tableaux.GetValueOrDefault(defi.Ruleset)?.Lignes.FirstOrDefault(l => string.Equals(l.Joueur, pseudo, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le temps publie du joueur, sinon son meilleur temps du journal de cette borne.</summary>
    private static string MonTemps(Reglement defi, IReadOnlyDictionary<string, (int Tentatives, long? Meilleur)> bilan,
        IReadOnlyDictionary<string, ClassementDEpreuve> tableaux, string pseudo)
        => (MaPlace(defi, tableaux, pseudo)?.Valeur ?? bilan.GetValueOrDefault(defi.ChallengeId).Meilleur) is { } temps
            ? Chronometrage.Afficher(temps, defi.Cadence) : SansTemps;

    private static bool EstUnSpeedrun(Reglement defi) => defi.Kind.StartsWith("speedrun_", StringComparison.Ordinal);

    /// <summary>
    /// LES TYPES DE RECORDS (§7.5), sans disque ni reseau. Une epreuve sans temps ne compte pas, et un type sans epreuve n'existe
    /// pas : sans aucun temps sur le jeu, l'onglet lui-meme n'existe pas. GLOBAL d'abord, puis chaque jeu complet sous son propre
    /// nom (Any%, All Emeralds : le sens est propre au jeu), puis NIVEAUX, BOSS et MONDES. Les challenges comptent dans GLOBAL et
    /// n'ont pas de type : leurs classements sont sur le site (31/10).
    /// </summary>
    internal IReadOnlyList<TypeDeRecords> ComposerLesRecords(IReadOnlyList<Reglement> defis,
        IReadOnlyDictionary<string, ClassementDEpreuve> tableaux, string langue, string pseudo)
    {
        var classees = defis.Where(d => LeRecord(d, tableaux) is not null).ToList();
        if (classees.Count == 0) return [];

        VueDeRecords Vue(Reglement d) => VueDe(d, tableaux[d.Ruleset], langue, pseudo);
        var speedruns = classees.Where(EstUnSpeedrun).ToList();
        var types = new List<TypeDeRecords> { new("GLOBAL", [LeGlobal(classees, tableaux, pseudo)]) };
        types.AddRange(speedruns.Where(d => Groupe(d.Kind) == "full").Select(d => new TypeDeRecords(d.Titre(langue).ToUpperInvariant(), [Vue(d)])));
        foreach (var (groupe, etiquette) in new[]
                 {
                     ("levels", T("speedrun.panel.records.levels", "LEVELS")),
                     ("bosses", T("speedrun.panel.records.bosses", "BOSSES")),
                     ("worlds", T("speedrun.panel.records.worlds", "WORLDS")),
                 })
        {
            var vues = speedruns.Where(d => Groupe(d.Kind) == groupe).Select(Vue).ToList();
            if (vues.Count > 0) types.Add(new TypeDeRecords(etiquette, vues));
        }

        return types;
    }

    /// <summary>GLOBAL : les joueurs par nombre de records detenus sur ce jeu, sans points. A egalite, meme rang.</summary>
    private VueDeRecords LeGlobal(IReadOnlyList<Reglement> classees, IReadOnlyDictionary<string, ClassementDEpreuve> tableaux, string pseudo)
    {
        var detenteurs = classees.GroupBy(d => tableaux[d.Ruleset].Lignes[0].Joueur, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Joueur: g.Key, Speedruns: g.Count(EstUnSpeedrun), Challenges: g.Count(d => !EstUnSpeedrun(d))))
            .OrderByDescending(j => j.Speedruns + j.Challenges).ThenBy(j => j.Joueur, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var avecChallenges = classees.Any(d => !EstUnSpeedrun(d));
        var lignes = new List<LigneDeDefi>();
        var rang = 0;
        for (var i = 0; i < detenteurs.Count; i++)
        {
            var j = detenteurs[i];
            var total = j.Speedruns + j.Challenges;
            if (i == 0 || total != detenteurs[i - 1].Speedruns + detenteurs[i - 1].Challenges) rang = i + 1;
            lignes.Add(avecChallenges
                ? new LigneDeDefi($"{rang}   {j.Joueur}", j.Speedruns.ToString(), j.Challenges.ToString())
                : new LigneDeDefi($"{rang}   {j.Joueur}", "", total.ToString()));
        }

        var moi = detenteurs.FindIndex(j => string.Equals(j.Joueur, pseudo, StringComparison.OrdinalIgnoreCase));
        return new VueDeRecords(T("speedrun.panel.records.global_rule", "Records held on this game"),
            avecChallenges ? ["SPEEDRUNS", "CHALLENGES"] : ["", "RECORDS"], lignes, moi >= 0 ? moi : null);
    }

    private VueDeRecords VueDe(Reglement defi, ClassementDEpreuve classement, string langue, string pseudo)
    {
        var lignes = classement.Lignes.Select(l => new LigneDeDefi($"{l.Rang}   {l.Joueur}", "", Chronometrage.Afficher(l.Valeur, defi.Cadence))).ToList();
        var moi = MaPlace(defi, new Dictionary<string, ClassementDEpreuve> { [defi.Ruleset] = classement }, pseudo);
        return new VueDeRecords(defi.Titre(langue) + " · " + Objectif(defi), ["", T("speedrun.panel.time", "TIME")], lignes, moi is null ? null : moi.Rang - 1);
    }

    /// <summary>Ce que dit le defi de lui-meme, sinon ce que dit son genre.</summary>
    private string Objectif(Reglement defi) => defi.Resume(Langue()) ?? ObjectifDuGenre(defi);

    private string ObjectifDuGenre(Reglement defi) => defi.Kind switch
    {
        "speedrun_level" => T("speedrun.panel.goal.level", "Finish the level alive"),
        "speedrun_boss" => T("speedrun.panel.goal.boss", "Defeat the boss alive"),
        "speedrun_world" => T("speedrun.panel.goal.world", "Finish the world in order"),
        "speedrun_full" => T("speedrun.panel.goal.full", "Reach the ending"),
        "challenge_boss" => T("speedrun.panel.goal.boss_rush", "Defeat the boss"),
        _ => T("speedrun.panel.goal.challenge", "Complete the challenge"),
    };

    /// <summary>
    /// Les lignes de chaque onglet, sans disque ni reseau. Un defi « speedrun_* » va dans SPEEDRUNS, tout autre dans
    /// CHALLENGES (le pattern du reglement le dira, P13). Les groupes suivent le genre : Niveaux, Boss, Mondes, Jeu complet.
    /// </summary>
    internal Dictionary<DefisPanelModel.Onglet, List<(LigneDeDefi Ligne, Reglement? Defi)>> Composer(
        IReadOnlyList<Reglement> defis, IReadOnlyDictionary<string, (int Tentatives, long? Meilleur)> bilan, string langue,
        IReadOnlyDictionary<string, ClassementDEpreuve>? classements = null, string pseudo = "",
        IReadOnlyDictionary<string, string>? images = null)
    {
        var tableaux = classements ?? new Dictionary<string, ClassementDEpreuve>();
        var resultat = new Dictionary<DefisPanelModel.Onglet, List<(LigneDeDefi, Reglement?)>>();
        foreach (var groupe in defis.GroupBy(d => (Onglet: d.Kind.StartsWith("speedrun_", StringComparison.Ordinal)
                     ? DefisPanelModel.Onglet.Speedruns : DefisPanelModel.Onglet.Challenges, Groupe: Groupe(d.Kind)))
                     .OrderBy(g => g.Key.Onglet).ThenBy(g => OrdreDuGroupe(g.Key.Groupe)))
        {
            if (!resultat.TryGetValue(groupe.Key.Onglet, out var lignes)) resultat[groupe.Key.Onglet] = lignes = new();
            lignes.Add((new LigneDeDefi(T("speedrun.panel.group." + groupe.Key.Groupe, NomDuGroupe(groupe.Key.Groupe)), Groupe: true), null));
            foreach (var d in groupe.OrderBy(d => d.Ordre).ThenBy(d => d.Titre(langue), StringComparer.CurrentCultureIgnoreCase))
            {
                var (tentatives, meilleur) = bilan.GetValueOrDefault(d.ChallengeId);
                var marque = meilleur is not null ? "reussi" : tentatives > 0 ? "tente" : "";
                var toi = MonTemps(d, bilan, tableaux, pseudo);
                var record = LeRecord(d, tableaux) is { } r ? Chronometrage.Afficher(r.Valeur, d.Cadence) : SansTemps;
                var badge = meilleur is not null ? T("speedrun.panel.done", "DONE")
                    : tentatives > 0 ? T("speedrun.panel.tried", "TRIED") + " × " + tentatives : "";
                lignes.Add((groupe.Key.Onglet == DefisPanelModel.Onglet.Challenges
                    ? new LigneDeDefi(d.Titre(langue), T("speedrun.panel.record", "RECORD") + " " + record, T("speedrun.panel.you", "YOU") + " " + toi,
                        Marque: marque, Regle: Objectif(d), Mesure: T("speedrun.panel.by_time", "BY TIME"), Badge: badge,
                        Image: images is not null && images.TryGetValue(d.ChallengeId, out var image) ? image : null)
                    : new LigneDeDefi(d.Titre(langue), record, toi, Marque: marque), d));
            }
        }

        return resultat;
    }

    private static string Groupe(string kind) => kind switch
    {
        "speedrun_level" => "levels",
        "speedrun_boss" => "bosses",
        "speedrun_world" => "worlds",
        "speedrun_full" => "full",
        // Le Boss Rush d'un jeu (Metal Slug 3, 13/11) : une epreuve par boss, sous les autres challenges du jeu.
        "challenge_boss" => "boss_rush",
        _ => "challenges",
    };

    private static int OrdreDuGroupe(string groupe) => groupe switch
    {
        "levels" => 0, "bosses" => 1, "worlds" => 2, "full" => 3, "boss_rush" => 5, _ => 4,
    };

    private static string NomDuGroupe(string groupe) => groupe switch
    {
        "levels" => "Levels",
        "bosses" => "Bosses",
        "worlds" => "Worlds",
        "full" => "Full game",
        "boss_rush" => "Boss Rush",
        _ => "Challenges",
    };

    private static List<(LigneDeDefi Ligne, Reglement? Defi)> Choisissables(List<(LigneDeDefi Ligne, Reglement? Defi)> lignes)
        => lignes.Where(l => l.Defi is not null).ToList();

    /// <summary>L'index, parmi les lignes choisissables, de la premiere epreuve de chaque groupe : LB et RB y sautent.</summary>
    private static IReadOnlyList<int> DebutsDeGroupe(List<(LigneDeDefi Ligne, Reglement? Defi)> lignes)
    {
        var debuts = new List<int>();
        var index = 0;
        var nouveauGroupe = false;
        foreach (var l in lignes)
        {
            if (l.Defi is null) { nouveauGroupe = true; continue; }
            if (nouveauGroupe) debuts.Add(index);
            nouveauGroupe = false;
            index++;
        }

        return debuts;
    }

    /// <summary>Les apercus se dessinent dans une langue choisie, sans reglages d'ES.</summary>
    internal string? LangueDApercu { get; init; }

    /// <summary>
    /// Essais et apercus : le panneau ouvert sur ces defis et entre (le joueur a la main), sans ES ni collection. Rend ce que
    /// la fenetre recevrait.
    /// </summary>
    internal ContenuDefis OuvrirPourEssai(IReadOnlyList<Reglement> defis)
    {
        lock (_verrou)
        {
            _defis = defis;
            _selecteur.Fermer();
            _modele.Ouvrir(Recomposer());
            PoserLesCompteurs();
            _modele.Gauche();
        }

        return Contenu();
    }

    /// <summary>Essais et apercus : ce que la fenetre montre maintenant.</summary>
    internal ContenuDefis ContenuPourEssai() => Contenu();

    private string Langue()
    {
        if (LangueDApercu is { } apercu) return apercu;
        try { return _reglages?.GetScrapingSettings().Language ?? "en"; }
        catch (Exception) { return "en"; }
    }

    private string T(string cle, string parDefaut)
    {
        var texte = _textes?.Text(cle, Langue());
        return string.IsNullOrEmpty(texte) || texte == cle ? parDefaut : texte;
    }
}
