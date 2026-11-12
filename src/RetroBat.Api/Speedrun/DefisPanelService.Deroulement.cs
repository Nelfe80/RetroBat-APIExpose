using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Events;
using static RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Speedrun;

/// <summary>La page plein ecran que montre le panneau des defis, hors du volet.</summary>
public enum PageDEpreuve
{
    Aucune,
    Preparation,
    Bilan,
}

/// <summary>
/// LE DEROULEMENT D'UNE EPREUVE LANCEE DEPUIS LE PANNEAU (lot P6, CDC du panneau §8) :
/// <list type="bullet">
/// <item>la preparation tient devant le jeu jusqu'a la premiere image lue par le wrapper (le lancement annonce le jeu des le
/// depart de RetroArch, bien avant qu'il soit a l'ecran), puis « A TOI ! » et l'effacement ; B maintenu ferme le jeu ;</item>
/// <item>le bilan s'ouvre a la fermeture de la session, avec le meilleur temps du journal ; le verdict du site (bus,
/// <c>scoring.verdict</c>) le complete : rang, record, voisins. A maintenu relance, A ou B ferme, et il se ferme seul.</item>
/// </list>
/// Un lancement par ES, hors du panneau, ne montre aucune page : il reste une partie du joueur.
/// </summary>
public sealed partial class DefisPanelService
{
    private static readonly TimeSpan TenueMax = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan GraceApresLaPremiereImage = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan DureeDuDepart = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan DureeDeLEffacement = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan GardeDuBilan = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DureeDuBilan = TimeSpan.FromSeconds(45);

    private sealed class EpreuveLancee(Reglement defi, string chemin, string jeu, string? fanart, DateTime lanceA, DateTime lanceALocal)
    {
        public Reglement Defi { get; } = defi;
        public string Chemin { get; } = chemin;
        public string Jeu { get; } = jeu;
        public string? Fanart { get; } = fanart;
        public DateTime LanceA { get; } = lanceA;
        public DateTime LanceALocal { get; } = lanceALocal;

        /// <summary>Le bilan du wrapper signale une triche ou un etat charge : rien de ce lancement ne part.</summary>
        public bool Sale { get; set; }
    }

    private readonly IDisposable? _abonnement;
    private string _nomDuJeu = "";
    private PageDEpreuve _page;
    private EpreuveLancee? _epreuve;
    private BilanDEpreuve? _bilanAffiche;
    private DateTime _bilanDepuis;
    private DateTime? _validerDepuis;
    private DateTime? _annulerDepuis;
    private CancellationTokenSource? _suiteDeLaPage;
    private (string Regle, string Statut, int? Rang, long? Score)? _verdictEnAttente;

    public PageDEpreuve Page
    {
        get { lock (_verrou) return _page; }
    }

    /// <summary>Une page est a l'ecran : elle prend les boutons Valider et Retour.</summary>
    public bool EnPage => Page != PageDEpreuve.Aucune;

    /// <summary>La preparation tient devant le jeu : l'annonce du jeu ne doit pas l'effacer.</summary>
    public bool TientLaPreparation => Page == PageDEpreuve.Preparation;

    // ── Le lancement ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lance l'epreuve et montre la preparation ; rend la raison d'un refus, ou null.
    /// <para>
    /// LA PAGE D'ABORD (13/11, « l'ecran d'intro des speedruns met du temps a s'afficher ») : elle attendait tout le lancement
    /// (fantomes, Deck, manettes d'ES, lanceur, abonnes de ui.game.started), de 7 a 26 s mesures sur la borne le 12/11, le
    /// panneau fige pendant ce temps. Les controles rapides passent d'abord (un refus se dit encore dans le panneau), la page
    /// s'ouvre, et le jeu part dessous (<see cref="PartirSousLaPage"/>) : elle tenait deja jusqu'a la premiere image.
    /// </para>
    /// </summary>
    private string? LancerLEpreuve(Reglement defi, string chemin, string jeu)
    {
        var lanceA = DateTime.UtcNow;
        var lanceALocal = DateTime.Now;
        var (verifie, refus) = _session.Verifier(defi.ChallengeId, (_, _) => File.Exists(chemin) ? chemin : null);
        if (verifie is null)
        {
            _journal?.LogInformation("Defis : lancement de {Defi} refuse ({Raison} {Detail}).", defi.ChallengeId, refus?.Raison, refus?.Detail);
            return refus?.Raison ?? "launcher_failed";
        }

        string? fanart = null;
        try { fanart = RetroBat.Api.Media.MediaDuGamelist.Resoudre(chemin, "fanart", "image"); }
        catch (Exception ex) { _journal?.LogDebug(ex, "Defis : fanart introuvable pour {Chemin}.", chemin); }
        var epreuve = new EpreuveLancee(defi, chemin, jeu, fanart, lanceA, lanceALocal);
        MontrerLaPreparation(epreuve);
        _journal?.LogInformation("Defis : preparation de {Defi} a l'ecran en {Ms:0} ms, le jeu part dessous.", defi.ChallengeId,
            (DateTime.UtcNow - lanceA).TotalMilliseconds);
        _ = Task.Run(() => PartirSousLaPage(verifie, epreuve));
        return null;
    }

    /// <summary>
    /// Le jeu part sous la page de preparation. Un refus tardif (verrous non ecrits, lanceur introuvable, jeu parti entre-temps)
    /// efface la page et rend ES : rien n'a ete lance.
    /// </summary>
    private void PartirSousLaPage(LancementVerifie verifie, EpreuveLancee epreuve)
    {
        var debut = DateTime.UtcNow;
        ReponseLancement reponse;
        try
        {
            reponse = _session.Partir(verifie, pages: true);
        }
        catch (Exception ex)
        {
            _journal?.LogWarning(ex, "Defis : lancement de {Defi} en echec sous la page.", epreuve.Defi.ChallengeId);
            reponse = new ReponseLancement(false, "launcher_failed", ex.Message);
        }

        if (reponse.Lance)
        {
            _journal?.LogInformation("Defis : {Defi} lance depuis le panneau ({Ms:0} ms sous la page).", epreuve.Defi.ChallengeId,
                (DateTime.UtcNow - debut).TotalMilliseconds);
            // ES ne sait pas qu'un jeu tourne : sa musique se tait jusqu'a la fin du jeu (SonDEmulationStation la rend ensuite).
            _sonDEs?.Couper();
            return;
        }

        _journal?.LogInformation("Defis : lancement de {Defi} refuse sous la page ({Raison} {Detail}), ES revient.", epreuve.Defi.ChallengeId,
            reponse.Raison, reponse.Detail);
        if (!LacherLaPreparation(epreuve)) return;
        AnnoncerLaPage("preparation", false);
        _overlay.Fermer();
        EmulatorForeground.FocusEmulationStation();
    }

    /// <summary>La preparation de cette epreuve, encore a l'ecran, s'en va (vrai) ; une autre page ou une autre epreuve reste (faux).</summary>
    private bool LacherLaPreparation(EpreuveLancee epreuve)
    {
        lock (_verrou)
        {
            if (_page != PageDEpreuve.Preparation || !ReferenceEquals(_epreuve, epreuve)) return false;
            _page = PageDEpreuve.Aucune;
            _epreuve = null;
            _suiteDeLaPage?.Cancel();
            return true;
        }
    }

    private RetroBat.Domain.Interfaces.IEventBus? _bus;

    /// <summary>
    /// LA PAGE PLEIN ECRAN ANNONCEE AU PLAYERDECK (deck.page, 09/11) : tant que la preparation est la, le Deck ne montre aucune
    /// valeur ; les epreuves du panneau gardent leurs pages (DeckCartes.Api), le Deck n'y ajoute pas son ecran de depart.
    /// </summary>
    private void AnnoncerLaPage(string page, bool ouverte)
    {
        try
        {
            _ = _bus?.PublishAsync(new RetroBat.Domain.Events.EventEnvelope
            {
                Type = "deck.page",
                Payload = new RetroBat.Api.Deck.DeckPage(page, ouverte, "api", DateTime.UtcNow),
            });
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Defis : page {Page} non annoncee au Deck.", page);
        }
    }

    private void MontrerLaPreparation(EpreuveLancee epreuve)
    {
        CancellationToken jeton;
        lock (_verrou)
        {
            _modele.Fermer();
            _page = PageDEpreuve.Preparation;
            _epreuve = epreuve;
            _course = null;
            _bilanDeCourse = null;
            _bilanAffiche = null;
            _verdictEnAttente = null;
            _validerDepuis = _annulerDepuis = null;
            _suiteDeLaPage?.Cancel();
            _suiteDeLaPage = new CancellationTokenSource();
            jeton = _suiteDeLaPage.Token;
        }

        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal),
            PleinEcran(lancement: ComposerLaPreparation(epreuve.Defi, epreuve.Jeu, epreuve.Fanart, false, Langue())));
        AnnoncerLaPage("preparation", true);
        _ = Task.Run(() => TenirLaPreparationAsync(epreuve, jeton));
    }

    /// <summary>
    /// Devant le jeu jusqu'a sa premiere image (150 s au plus), en repassant devant chaque demi-seconde : la fenetre de
    /// RetroArch prend le dessus en paraissant. Puis « A TOI ! » 1,3 s, et l'effacement.
    /// </summary>
    private async Task TenirLaPreparationAsync(EpreuveLancee epreuve, CancellationToken jeton)
    {
        try
        {
            var limite = DateTime.UtcNow + TenueMax;
            var arrive = false;
            while (DateTime.UtcNow < limite)
            {
                await Task.Delay(500, jeton).ConfigureAwait(false);
                // Un depart sur etat : le jeu est la quand l'etat est pose, pret a charger, pas a sa premiere image (le BIOS, le
                // titre, qu'aucune image du wrapper ne dit avant le chargement).
                var arrivee = epreuve.Defi.Etat is not null ? _session.EtatPoseUtc : _session.PremiereImageUtc;
                if (arrivee is { } image && image >= epreuve.LanceA)
                {
                    arrive = true;
                    break;
                }

                _overlay.GarderDevantLeJeu();
            }

            if (arrive) await Task.Delay(GraceApresLaPremiereImage, jeton).ConfigureAwait(false);
            else _journal?.LogWarning("Defis : aucune image du jeu en {Secondes:F0} s, la preparation s'efface.", TenueMax.TotalSeconds);

            lock (_verrou)
            {
                if (_page != PageDEpreuve.Preparation || !ReferenceEquals(_epreuve, epreuve)) return;
            }

            _overlay.GarderDevantLeJeu();
            _overlay.MontrerMultiplayer(PleinEcran(lancement: ComposerLaPreparation(epreuve.Defi, epreuve.Jeu, epreuve.Fanart, true, Langue(), DateTime.UtcNow)));
            await Task.Delay(DureeDuDepart, jeton).ConfigureAwait(false);
            // Le depart sur etat se charge ici, sous la page qui s'efface : le joueur le decouvre a sa premiere image, et le
            // chrono (2,7 s plus tard sur Green Hill 1, 1,75 s sur Metal Slug 3) ne part jamais derriere la page.
            if (epreuve.Defi.Etat is not null) _session.ChargerLEtatDeDepart();
            AnnoncerLaPage("preparation", false);
            _overlay.Estomper(DureeDeLEffacement);
            await Task.Delay(DureeDeLEffacement, jeton).ConfigureAwait(false);
            lock (_verrou)
            {
                if (_page != PageDEpreuve.Preparation) return;
                _page = PageDEpreuve.Aucune;
            }

            _overlay.FermerPourLeJeu();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>B maintenu pendant la preparation : la page s'en va, ES revient, et le jeu se ferme des qu'il ecoute.</summary>
    private void QuitterLaPreparation()
    {
        EpreuveLancee? epreuve;
        CourseRejointe? course;
        lock (_verrou)
        {
            if (_page != PageDEpreuve.Preparation) return;
            epreuve = _epreuve;
            course = _course;
            _page = PageDEpreuve.Aucune;
            _epreuve = null;
            _course = null;
            _suiteDeLaPage?.Cancel();
        }

        _journal?.LogInformation("Defis : le joueur quitte la preparation de {Defi}.", epreuve?.Defi.ChallengeId ?? "MASS RUN");
        AnnoncerLaPage("preparation", false);
        _overlay.Fermer();
        EmulatorForeground.FocusEmulationStation();
        if (epreuve is not null) _ = Task.Run(() => FermerLeJeuAsync(epreuve.LanceALocal));
        if (course is not null) _ = Task.Run(() => FermerLeJeuAsync(course.LanceALocal));
    }

    /// <summary>RetroArch n'entend QUIT qu'une fois charge : on insiste jusqu'a ce que le jeu de ce lancement soit parti.</summary>
    private static async Task FermerLeJeuAsync(DateTime lanceALocal)
    {
        var apparu = false;
        for (var essai = 0; essai < 120; essai++)
        {
            if (EmulatorForeground.EmulateurDeJeuTourne(lanceALocal.AddSeconds(-1)))
            {
                apparu = true;
                await RetroBat.Api.Netplay.RetroArchInvite.FermerAsync().ConfigureAwait(false);
            }
            else if (apparu)
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    // ── Le bilan ─────────────────────────────────────────────────────────────────

    private void SurEvenement(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "speedrun.session.closed":
                    var session = Texte(Racine(e.Payload), "sessionId");
                    if (session.Length > 0) _ = Task.Run(() => QuandLaSessionSeFermeAsync(session));
                    break;
                case "scoring.listener.session":
                    NoterLeBilanDuWrapper(Racine(e.Payload));
                    break;
                case "scoring.verdict":
                    var verdict = Racine(e.Payload);
                    _ = Task.Run(() => AppliquerLeVerdictAsync(Texte(verdict, "Ruleset"), Texte(verdict, "Status"), Entier(verdict, "Rank"),
                        verdict.TryGetProperty("Score", out var s) && s.TryGetInt64(out var score) ? score : null));
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "Defis : evenement {Type} ignore.", e.Type);
        }
    }

    private async Task QuandLaSessionSeFermeAsync(string sessionId)
    {
        EpreuveLancee? epreuve;
        bool preparation;
        lock (_verrou)
        {
            epreuve = _epreuve;
            preparation = _page == PageDEpreuve.Preparation;
        }

        if (epreuve is null) return;

        // Le jeu s'est ferme avant d'avoir paru : la preparation ne tient plus rien.
        if (preparation)
        {
            lock (_verrou)
            {
                _page = PageDEpreuve.Aucune;
                _suiteDeLaPage?.Cancel();
            }

            AnnoncerLaPage("preparation", false);
            _overlay.Fermer();
        }

        var bilan = _session.BilanDeLaSession(sessionId);
        if (bilan is null || !string.Equals(bilan.DefiId, epreuve.Defi.ChallengeId, StringComparison.OrdinalIgnoreCase))
        {
            // Aucune tentative partie : le joueur a quitte avant le depart, il n'y a rien a dire.
            lock (_verrou)
            {
                if (ReferenceEquals(_epreuve, epreuve)) _epreuve = null;
            }

            EmulatorForeground.FocusEmulationStation();
            return;
        }

        // ES reprend l'ecran quand l'emulateur s'en va : le bilan passe devant lui, pas l'inverse.
        await Task.Delay(400).ConfigureAwait(false);
        OuvrirLeBilan(BilanDe(epreuve.Defi, epreuve.Jeu, epreuve.Fanart, bilan, epreuve.Sale));
    }

    internal BilanDEpreuve BilanDe(Reglement defi, string jeu, string? fanart, BilanDeSession s, bool sale)
    {
        var joueur = Pseudo.Length > 0 ? Pseudo : T("speedrun.page.player", "PLAYER");
        if (s.Meilleur is not { } temps)
            return new BilanDEpreuve(defi, jeu, joueur, fanart, IssueDuBilan.AucunTemps, null, s.Tentatives, s.Finies, Motif: MotifDeFin(s.DernierEtat, s.DerniereCause));
        return new BilanDEpreuve(defi, jeu, joueur, fanart, sale ? IssueDuBilan.NonEnvoye : IssueDuBilan.EnVerification, temps, s.Tentatives, s.Finies,
            Motif: sale ? T("speedrun.page.reason.dirty", "Cheat or state load detected during this launch") : "");
    }

    /// <summary>Pourquoi la derniere tentative n'a pas donne de temps.</summary>
    internal string MotifDeFin(string? etat, string? cause) => (etat, cause) switch
    {
        (_, "pause" or "PAUSE_ON") => T("speedrun.page.reason.pause", "Last attempt voided: pause"),
        (_, "fast_forward") => T("speedrun.page.reason.fast_forward", "Last attempt voided: fast forward"),
        (_, { } c) when c.StartsWith("LEVEL_CHEAT", StringComparison.Ordinal) => T("speedrun.page.reason.debug", "Last attempt voided: debug mode"),
        (_, "LOSE_LIFE") => T("speedrun.page.reason.life", "Last attempt: life lost"),
        (SessionSpeedrun.Invalid, _) => T("speedrun.page.reason.voided", "Last attempt voided"),
        _ => T("speedrun.page.reason.unfinished", "Last attempt not finished"),
    };

    /// <summary>Le sort du temps d'apres le statut du site : « held » et « quarantined » le gardent sans le classer.</summary>
    internal static IssueDuBilan IssueDuStatut(string? statut) => statut switch
    {
        "published" => IssueDuBilan.Classe,
        "held" or "quarantined" => IssueDuBilan.Retenu,
        null or "" => IssueDuBilan.EnVerification,
        _ => IssueDuBilan.Refuse,
    };

    internal static BilanDEpreuve AvecLeVerdict(BilanDEpreuve b, IssueDuBilan issue, int? rang, long? score, ClassementDEpreuve? classement) => b with
    {
        Issue = issue,
        Temps = score ?? b.Temps,
        Rang = issue == IssueDuBilan.Classe ? rang : null,
        Record = classement?.Lignes.FirstOrDefault()?.Valeur,
        Classes = classement?.Total,
        Classement = issue == IssueDuBilan.Classe ? classement?.Lignes : null,
    };

    private void OuvrirLeBilan(BilanDEpreuve b)
    {
        CancellationToken jeton;
        (string Regle, string Statut, int? Rang, long? Score)? enAttente;
        lock (_verrou)
        {
            _page = PageDEpreuve.Bilan;
            _bilanAffiche = b;
            _bilanDepuis = DateTime.UtcNow;
            _validerDepuis = _annulerDepuis = null;
            _suiteDeLaPage?.Cancel();
            _suiteDeLaPage = new CancellationTokenSource();
            jeton = _suiteDeLaPage.Token;
            enAttente = _verdictEnAttente;
            _verdictEnAttente = null;
        }

        _journal?.LogInformation("Defis : bilan de {Defi} : {Issue}, {Tentatives} tentative(s), meilleur {Temps}.", b.Defi.ChallengeId, b.Issue, b.Tentatives,
            b.Temps is { } t ? Chronometrage.Afficher(t, b.Defi.Cadence) : "-");
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), PleinEcran(fin: ComposerLeBilan(b, Langue())));
        _ = Task.Run(async () =>
        {
            try
            {
                // La fenetre parait sur son fil : la manette se prend un instant apres.
                await Task.Delay(300, jeton).ConfigureAwait(false);
                if (!_overlay.PrendreLeFocus()) _journal?.LogInformation("Defis : le bilan n'a pas pu prendre la manette, ES la garde.");
                await Task.Delay(DureeDuBilan, jeton).ConfigureAwait(false);
                FermerLeBilan();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        if (enAttente is { } v) _ = Task.Run(() => AppliquerLeVerdictAsync(v.Regle, v.Statut, v.Rang, v.Score));
    }

    private void NoterLeBilanDuWrapper(JsonElement resume)
    {
        if (Texte(resume, "Session") is not { Length: > 0 } session) return;
        EpreuveLancee? lancee;
        lock (_verrou) lancee = _epreuve;
        // Le chargement de l'etat de depart n'est pas une sauvegarde chargee.
        if (lancee is null || PasseportDeDefi.Salissure(session, lancee.Defi.Etat is null ? 0 : 1) is not { } motif) return;
        BilanDEpreuve? b;
        lock (_verrou)
        {
            if (_epreuve is not { } epreuve || !string.Equals(Texte(resume, "Rom"), epreuve.Defi.RomGroup, StringComparison.OrdinalIgnoreCase)) return;
            epreuve.Sale = true;
            b = _page == PageDEpreuve.Bilan && _bilanAffiche is { Issue: IssueDuBilan.EnVerification } enCours
                ? enCours with { Issue = IssueDuBilan.NonEnvoye, Motif = T("speedrun.page.reason.dirty", "Cheat or state load detected during this launch") }
                : null;
            if (b is not null) _bilanAffiche = b;
        }

        _journal?.LogInformation("Defis : le bilan du wrapper signale {Motif}, aucun temps de ce lancement ne part.", motif);
        if (b is not null) _overlay.MontrerMultiplayer(PleinEcran(fin: ComposerLeBilan(b, Langue())));
    }

    /// <summary>Le verdict de notre epreuve : le bilan se complete, et le classement se relit pour montrer le record et les voisins.</summary>
    private async Task AppliquerLeVerdictAsync(string regle, string statut, int? rang, long? score)
    {
        BilanDEpreuve? b;
        lock (_verrou)
        {
            if (_epreuve is null || !string.Equals(regle, _epreuve.Defi.Ruleset, StringComparison.Ordinal)) return;
            b = _page == PageDEpreuve.Bilan ? _bilanAffiche : null;
            if (b is null)
            {
                // Le verdict peut devancer l'ouverture du bilan : il l'attend.
                _verdictEnAttente = (regle, statut, rang, score);
                return;
            }
        }

        var issue = IssueDuStatut(statut);
        ClassementDEpreuve? classement = null;
        if (issue == IssueDuBilan.Classe && _classements is not null)
            classement = (await _classements.ChargerAsync([regle], CancellationToken.None, forcer: true).ConfigureAwait(false)).GetValueOrDefault(regle);

        var complet = AvecLeVerdict(b, issue, rang, score, classement);
        lock (_verrou)
        {
            if (!ReferenceEquals(_bilanAffiche, b)) return;
            _bilanAffiche = complet;
        }

        _journal?.LogInformation("Defis : verdict de {Defi} : {Statut}{Rang}.", b.Defi.ChallengeId, statut, rang is { } r ? ", rang " + r : "");
        _overlay.MontrerMultiplayer(PleinEcran(fin: ComposerLeBilan(complet, Langue())));
    }

    private void FermerLeBilan()
    {
        lock (_verrou)
        {
            if (_page != PageDEpreuve.Bilan) return;
            _page = PageDEpreuve.Aucune;
            _bilanAffiche = null;
            _epreuve = null;
            _bilanDeCourse = null;
            _course = null;
            _suiteDeLaPage?.Cancel();
        }

        _overlay.Fermer();
        EmulatorForeground.FocusEmulationStation();
    }

    private void RelancerDepuisLeBilan()
    {
        bool course;
        lock (_verrou) course = _page == PageDEpreuve.Bilan && _course is not null;
        if (course)
        {
            CourseSuivante();
            return;
        }

        EpreuveLancee? epreuve;
        lock (_verrou)
        {
            if (_page != PageDEpreuve.Bilan || _epreuve is null) return;
            epreuve = _epreuve;
            _page = PageDEpreuve.Aucune;
            _bilanAffiche = null;
            _suiteDeLaPage?.Cancel();
        }

        _journal?.LogInformation("Defis : {Defi} relance depuis le bilan.", epreuve.Defi.ChallengeId);
        if (LancerLEpreuve(epreuve.Defi, epreuve.Chemin, epreuve.Jeu) is not null)
        {
            lock (_verrou) _epreuve = null;
            _overlay.Fermer();
            EmulatorForeground.FocusEmulationStation();
        }
    }

    // ── Les boutons sur une page ─────────────────────────────────────────────────

    /// <summary>Valider sur une page : au bilan, maintenu il relance, relache court il ferme. La preparation l'ignore.</summary>
    public void ValiderSurLaPage(bool appuye)
    {
        bool maintenu;
        PageDEpreuve page;
        bool garde;
        lock (_verrou)
        {
            if (appuye)
            {
                _validerDepuis = DateTime.UtcNow;
                return;
            }

            maintenu = Maintenu(_validerDepuis);
            _validerDepuis = null;
            page = _page;
            garde = DateTime.UtcNow - _bilanDepuis < GardeDuBilan;
        }

        if (page != PageDEpreuve.Bilan || garde) return;
        if (maintenu) RelancerDepuisLeBilan();
        else FermerLeBilan();
    }

    /// <summary>Retour sur une page : maintenu pendant la preparation, il ferme le jeu ; au bilan, il ferme la page.</summary>
    public void AnnulerSurLaPage(bool appuye)
    {
        bool maintenu;
        PageDEpreuve page;
        bool garde;
        lock (_verrou)
        {
            if (appuye)
            {
                _annulerDepuis = DateTime.UtcNow;
                return;
            }

            maintenu = Maintenu(_annulerDepuis);
            _annulerDepuis = null;
            page = _page;
            garde = DateTime.UtcNow - _bilanDepuis < GardeDuBilan;
        }

        if (page == PageDEpreuve.Preparation && maintenu) QuitterLaPreparation();
        else if (page == PageDEpreuve.Bilan && !garde) FermerLeBilan();
    }

    private bool Maintenu(DateTime? depuis)
        => depuis is { } debut && (DateTime.UtcNow - debut).TotalMilliseconds >= Math.Max(200, _options.CurrentValue.Leaderboard.LongPressMs);

    // ── Outils ───────────────────────────────────────────────────────────────────

    private static ContenuMultiplayer PleinEcran(EcranDeLancement? lancement = null, EcranDeFin? fin = null)
        => new([string.Empty], 0, false, string.Empty, string.Empty, string.Empty, null, [], -1, string.Empty, string.Empty, [],
            PleinEcran: true, Lancement: lancement, Fin: fin);

    private static JsonElement Racine(object? payload) => payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);

    private static string Texte(JsonElement racine, string nom)
        => racine.ValueKind == JsonValueKind.Object && racine.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Entier(JsonElement racine, string nom)
        => racine.ValueKind == JsonValueKind.Object && racine.TryGetProperty(nom, out var v) && v.TryGetInt32(out var n) ? n : null;

    private static bool CheminsEgaux(string? a, string? b)
        => string.Equals((a ?? "").Replace('\\', '/'), (b ?? "").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Le nom du jeu d'apres son fichier, sans les regions : « Sonic The Hedgehog (USA, Europe) » devient « Sonic The Hedgehog ».</summary>
    internal static string NomDuFichier(string chemin)
    {
        var nom = Path.GetFileNameWithoutExtension(chemin);
        var parenthese = nom.IndexOf(" (", StringComparison.Ordinal);
        return parenthese > 0 ? nom[..parenthese] : nom;
    }
}
