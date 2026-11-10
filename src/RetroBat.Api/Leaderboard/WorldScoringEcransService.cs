using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Services;
using static RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LE DEROULEMENT DES ECRANS DU WORLD SCORING (decision P-D7 du 10/11, choix (a) du user), sur le modele des pages des
/// epreuves du panneau (<c>DefisPanelService.Deroulement</c>) :
/// <list type="bullet">
/// <item>Depart : des l'appui sur CHALLENGE, a la place de la boite « Lancement de la partie ». Il tient devant le jeu jusqu'a
/// ce que le jeu soit la (fenetre de l'emulateur ET premiere valeur lue par le wrapper), puis « YOUR TURN! » 1,3 s et
/// l'effacement. La certification s'y met a jour (le bandeau du scoring). B maintenu ferme le jeu.</item>
/// <item>Bilan : a la fin de la partie, devant le retour a EmulationStation ; le verdict du site le complete (rang certifie,
/// continues, duree), les stats du PlayerDeck aussi (deck.recap). A maintenu relance le defi, A ou B ferme, et il se ferme
/// seul au bout de 45 s.</item>
/// </list>
/// Le PlayerDeck s'efface devant ces pages (deck.page, Cards = api), comme devant celles des epreuves. Lecture seule : rien
/// ici ne touche au score, au passeport ni a la certification.
/// </summary>
public sealed class WorldScoringEcransService : IHostedService, IDisposable
{
    private static readonly TimeSpan TenueMax = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan GraceApresLeJeu = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan DureeDuDepart = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan DureeDeLEffacement = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan GardeDuBilan = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DureeDuBilan = TimeSpan.FromSeconds(45);

    private enum Page { Aucune, Depart, Bilan }

    /// <summary>Un defi suivi, de l'appui sur CHALLENGE a la fermeture de son bilan.</summary>
    private sealed class Suivi(DepartDuScore depart, IReadOnlyList<LeaderboardClient.Ligne> monde, string regle, Action? rejouer)
    {
        public DepartDuScore Depart { get; set; } = depart;
        public IReadOnlyList<LeaderboardClient.Ligne> Monde { get; } = monde;
        public string Regle { get; } = regle;
        public Action? Rejouer { get; } = rejouer;
        public DateTime LanceA { get; } = DateTime.UtcNow;
        public DateTime LanceALocal { get; } = DateTime.Now;
        public DateTime? PremierSignal;
        public DateTime? JeuDemarre;
        public DateTime? JeuFini;
        public long? ScoreLu;
        public long? ScoreCertifie;
        public string Prevol = "";
        public (string Statut, int? Rang, long? Score, long? Continues, long? DureeMs)? Verdict;
        public string DeckNom = "";
        public IReadOnlyList<LigneDuDeck> Deck = [];
        public int? ViesPerdues;
        public BilanDuScore? Bilan;
    }

    private readonly IEventBus _bus;
    private readonly LeaderboardOverlayService _overlay;
    private readonly IOptionsMonitor<ApiExposeOptions>? _options;
    private readonly InterfaceTextService? _textes;
    private readonly EmulationStationSettingsService? _reglages;
    private readonly ILogger<WorldScoringEcransService>? _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private Page _page;
    private Suivi? _suivi;
    private string _glypheValider = "a";
    private string _glypheAnnuler = "b";
    private DateTime _bilanDepuis;
    private DateTime? _validerDepuis;
    private DateTime? _annulerDepuis;
    private CancellationTokenSource? _suiteDeLaPage;

    public WorldScoringEcransService(IEventBus bus, LeaderboardOverlayService overlay, IOptionsMonitor<ApiExposeOptions>? options = null,
        InterfaceTextService? textes = null, EmulationStationSettingsService? reglages = null, ILogger<WorldScoringEcransService>? journal = null)
    {
        _bus = bus;
        _overlay = overlay;
        _options = options;
        _textes = textes;
        _reglages = reglages;
        _journal = journal;
    }

    /// <summary>Les ecrans du World Scoring sont-ils voulus (reglage, vrai par defaut) ?</summary>
    public bool Actifs => _options?.CurrentValue.Leaderboard.WorldScoringScreens ?? true;

    /// <summary>Le depart tient devant le jeu : l'annonce du jeu (ui.game.started) ne doit pas l'effacer.</summary>
    public bool TientLeDepart
    {
        get { lock (_verrou) return _page == Page.Depart; }
    }

    /// <summary>Une page est a l'ecran : elle prend les boutons Valider et Retour.</summary>
    public bool EnPage
    {
        get { lock (_verrou) return _page != Page.Aucune; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        LiveContestOverlayService.AnnonceDuScoring += SurLAnnonceDuScoring;
        LiveContestOverlayService.PageDuScoringDevant = () => TientLeDepart;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _abonnement?.Dispose();
        _abonnement = null;
        LiveContestOverlayService.AnnonceDuScoring -= SurLAnnonceDuScoring;
        LiveContestOverlayService.PageDuScoringDevant = null;
    }

    // ── Le depart ────────────────────────────────────────────────────────────────

    /// <summary>
    /// L'appui sur CHALLENGE : l'ecran de depart paraît a la place de la boite d'attente. <paramref name="regle"/> : la regle
    /// du classement vise (1cc...), pour reconnaitre son verdict ; <paramref name="rejouer"/> : relancer le meme defi depuis le
    /// bilan.
    /// </summary>
    public void Ouvrir(DepartDuScore depart, IReadOnlyList<LeaderboardClient.Ligne> monde, string regle, string glypheValider, string glypheAnnuler,
        Action? rejouer = null)
    {
        var suivi = new Suivi(depart, monde, regle, rejouer);
        CancellationToken jeton;
        lock (_verrou)
        {
            _suivi = suivi;
            _page = Page.Depart;
            _glypheValider = glypheValider.Length > 0 ? glypheValider : "a";
            _glypheAnnuler = glypheAnnuler.Length > 0 ? glypheAnnuler : "b";
            _validerDepuis = _annulerDepuis = null;
            _suiteDeLaPage?.Cancel();
            _suiteDeLaPage = new CancellationTokenSource();
            jeton = _suiteDeLaPage.Token;
        }

        _journal?.LogInformation("World Scoring : ecran de depart de {Jeu} ({Mode}), cible {Cible}.", depart.Jeu, depart.Mode,
            depart.Cible is { } c ? "#" + c.Rang + " " + c.Valeur : "aucune");
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), PleinEcran(lancement: ComposerLeDepart(suivi, false, null)));
        AnnoncerLaPage("ws-start", true);
        _ = Task.Run(() => TenirLeDepartAsync(suivi, jeton));
    }

    /// <summary>Le lancement n'a pas eu lieu (ES a refuse, erreur) : le depart s'en va, plus rien n'est suivi.</summary>
    public void Abandonner(string raison)
    {
        bool depart;
        lock (_verrou)
        {
            if (_suivi is null) return;
            depart = _page == Page.Depart;
            _suivi = null;
            _page = Page.Aucune;
            _suiteDeLaPage?.Cancel();
        }

        _journal?.LogInformation("World Scoring : ecrans abandonnes ({Raison}).", raison);
        if (depart) AnnoncerLaPage("ws-start", false);
    }

    private EcranDeLancement ComposerLeDepart(Suivi s, bool jeuPret, DateTime? depart)
        => WorldScoringEcrans.ComposerLeDepart(s.Depart, T, jeuPret, depart, _glypheAnnuler, _glypheValider);

    /// <summary>
    /// Devant le jeu jusqu'a ce qu'il soit la (150 s au plus), en repassant devant chaque demi-seconde : la fenetre de l'emulateur
    /// prend le dessus en paraissant. Le jeu est la quand sa fenetre tourne ET que le wrapper lit ses premieres valeurs (le coeur
    /// tourne, ses images sont a l'ecran) ; puis « YOUR TURN! » 1,3 s, et l'effacement.
    /// </summary>
    private async Task TenirLeDepartAsync(Suivi suivi, CancellationToken jeton)
    {
        try
        {
            var limite = DateTime.UtcNow + TenueMax;
            var arrive = false;
            while (DateTime.UtcNow < limite)
            {
                await Task.Delay(500, jeton).ConfigureAwait(false);
                if (suivi.PremierSignal is not null && EmulatorForeground.EmulateurDeJeuTourne(suivi.LanceALocal.AddSeconds(-1)))
                {
                    arrive = true;
                    break;
                }

                _overlay.GarderDevantLeJeu();
            }

            if (arrive) await Task.Delay(GraceApresLeJeu, jeton).ConfigureAwait(false);
            else _journal?.LogWarning("World Scoring : le jeu n'est pas venu en {Secondes:F0} s, l'ecran de depart s'efface.", TenueMax.TotalSeconds);

            lock (_verrou)
            {
                if (_page != Page.Depart || !ReferenceEquals(_suivi, suivi)) return;
            }

            _overlay.GarderDevantLeJeu();
            if (arrive)
            {
                _overlay.MontrerMultiplayer(PleinEcran(lancement: ComposerLeDepart(suivi, true, DateTime.UtcNow)));
                await Task.Delay(DureeDuDepart, jeton).ConfigureAwait(false);
            }

            AnnoncerLaPage("ws-start", false);
            _overlay.Estomper(DureeDeLEffacement);
            await Task.Delay(DureeDeLEffacement, jeton).ConfigureAwait(false);
            lock (_verrou)
            {
                if (_page != Page.Depart) return;
                _page = Page.Aucune;
                if (!arrive) _suivi = null;
            }

            _overlay.FermerPourLeJeu();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>B maintenu pendant le depart : l'ecran s'en va, ES revient, et le jeu se ferme des qu'il ecoute.</summary>
    private void QuitterLeDepart()
    {
        Suivi? suivi;
        lock (_verrou)
        {
            if (_page != Page.Depart) return;
            suivi = _suivi;
            _page = Page.Aucune;
            _suivi = null;
            _suiteDeLaPage?.Cancel();
        }

        _journal?.LogInformation("World Scoring : le joueur quitte l'ecran de depart.");
        AnnoncerLaPage("ws-start", false);
        _overlay.Fermer();
        EmulatorForeground.FocusEmulationStation();
        if (suivi is not null) _ = Task.Run(() => FermerLeJeuAsync(suivi.LanceALocal));
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

    // ── Les evenements ───────────────────────────────────────────────────────────

    private void Recevoir(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "score.live.changed":
                    NoterLeScore(e.Payload);
                    break;
                case "retroarch.memory.changed":
                case "ingame.memory.changed":
                    lock (_verrou)
                    {
                        if (_suivi is { PremierSignal: null } s) s.PremierSignal = DateTime.UtcNow;
                    }
                    break;
                case "ui.game.started":
                    lock (_verrou)
                    {
                        if (_suivi is { JeuDemarre: null } s) s.JeuDemarre = DateTime.UtcNow;
                    }
                    break;
                case "ui.game.ended":
                    _ = Task.Run(QuandLeJeuSeFermeAsync);
                    break;
                case "scoring.verdict":
                    NoterLeVerdict(Racine(e.Payload));
                    break;
                case "deck.recap":
                    NoterLeDeck(Racine(e.Payload));
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "World Scoring : evenement {Type} ignore.", e.Type);
        }
    }

    private void NoterLeScore(object? payload)
    {
        long score;
        int joueur;
        if (payload is ScoreLiveEvent s)
        {
            (score, joueur) = (s.Score, s.Player);
        }
        else
        {
            var r = Racine(payload);
            if (!r.TryGetProperty("Score", out var v) || !v.TryGetInt64(out score)) return;
            joueur = r.TryGetProperty("Player", out var p) && p.TryGetInt32(out var n) ? n : 1;
        }

        if (joueur != 1) return;
        lock (_verrou)
        {
            if (_suivi is not { } suivi || suivi.JeuFini is not null) return;
            suivi.PremierSignal ??= DateTime.UtcNow;
            suivi.ScoreLu = score;
        }
    }

    /// <summary>
    /// Le bandeau du scoring (LiveContestOverlayService.ShowTop) : la certification au depart ; le score certifie quand un
    /// continue ou un credit le fige (« scoring_continue »...) ; une partie devenue non certifiable pour le bilan.
    /// </summary>
    private void SurLAnnonceDuScoring(RetroBat.Api.Deck.DeckNotice n)
    {
        EcranDeLancement? aRedessiner = null;
        lock (_verrou)
        {
            if (_suivi is not { } suivi) return;
            if (n.Kind is "scoring_continue" or "scoring_credit" or "scoring_joined" or "scoring_clear")
            {
                if (long.TryParse((n.Value ?? "").Replace(",", "").Replace(" ", ""), out var fige)) suivi.ScoreCertifie ??= fige;
                return;
            }

            if (!n.Kind.StartsWith("scoring_", StringComparison.Ordinal)) return;
            suivi.Prevol = n.Kind;
            if (_page == Page.Depart)
            {
                suivi.Depart = suivi.Depart with { Prevol = n.Kind, PrevolUtc = DateTime.UtcNow };
                aRedessiner = ComposerLeDepart(suivi, false, null);
            }
        }

        if (aRedessiner is not null) _overlay.MontrerMultiplayer(PleinEcran(lancement: aRedessiner));
    }

    private void NoterLeVerdict(JsonElement v)
    {
        var regle = Texte(v, "Ruleset");
        Suivi? suivi;
        lock (_verrou)
        {
            suivi = _suivi;
            if (suivi is null || suivi.JeuDemarre is null && suivi.PremierSignal is null) return;
            // Un jeu a plusieurs classements (1CC, 1LC) : celui du defi, sinon le premier venu.
            var notre = suivi.Regle.Length == 0 || MemeRegle(regle, suivi.Regle);
            if (!notre && suivi.Verdict is not null) return;
            if (!notre && suivi.Regle.Length > 0) return;
            suivi.Verdict = (Texte(v, "Status"), Entier(v, "Rank"), Long(v, "Score"), Long(v, "Continues"), Long(v, "DurationMs"));
        }

        _journal?.LogInformation("World Scoring : verdict {Regle} {Statut}.", regle, Texte(v, "Status"));
        Recomposer();
    }

    /// <summary>La regle du verdict (celle du passeport) est-elle celle du defi ? Comparaison souple : « ket:1cc » vaut « 1cc ».</summary>
    internal static bool MemeRegle(string duVerdict, string duDefi)
    {
        if (duVerdict.Length == 0 || duDefi.Length == 0) return false;
        if (string.Equals(duVerdict, duDefi, StringComparison.OrdinalIgnoreCase)) return true;
        var fin = duVerdict.Split(':', '/', '|').Last();
        return string.Equals(fin, duDefi, StringComparison.OrdinalIgnoreCase);
    }

    private void NoterLeDeck(JsonElement r)
    {
        var lignes = new List<LigneDuDeck>();
        if (r.TryGetProperty("Rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var libelle = Texte(row, "Label");
                var valeur = Texte(row, "Value");
                if (libelle.Length > 0 && valeur.Length > 0) lignes.Add(new LigneDuDeck(libelle, valeur));
            }
        }

        lock (_verrou)
        {
            if (_suivi is not { } suivi) return;
            suivi.Deck = lignes;
            suivi.DeckNom = Texte(r, "DeckName");
            suivi.ViesPerdues = Entier(r, "LivesLost") is >= 0 and var vies ? vies : null;
        }

        Recomposer();
    }

    // ── Le bilan ─────────────────────────────────────────────────────────────────

    private async Task QuandLeJeuSeFermeAsync()
    {
        Suivi? suivi;
        bool depart;
        lock (_verrou)
        {
            suivi = _suivi;
            if (suivi is null || suivi.JeuFini is not null || _page == Page.Bilan) return;
            suivi.JeuFini = DateTime.UtcNow;
            depart = _page == Page.Depart;
        }

        if (suivi.PremierSignal is null && suivi.JeuDemarre is null)
        {
            // Le jeu s'est ferme avant d'avoir paru : il n'y a rien a dire.
            Abandonner("jeu ferme avant d'avoir paru");
            if (depart) _overlay.Fermer();
            return;
        }

        if (depart)
        {
            lock (_verrou) _suiteDeLaPage?.Cancel();
            AnnoncerLaPage("ws-start", false);
        }

        // ES reprend l'ecran quand l'emulateur s'en va : le bilan passe devant lui, pas l'inverse.
        await Task.Delay(400).ConfigureAwait(false);
        OuvrirLeBilan(suivi);
    }

    private void OuvrirLeBilan(Suivi suivi)
    {
        CancellationToken jeton;
        BilanDuScore bilan;
        lock (_verrou)
        {
            if (!ReferenceEquals(_suivi, suivi)) return;
            bilan = BilanDe(suivi);
            suivi.Bilan = bilan;
            _page = Page.Bilan;
            _bilanDepuis = DateTime.UtcNow;
            _validerDepuis = _annulerDepuis = null;
            _suiteDeLaPage?.Cancel();
            _suiteDeLaPage = new CancellationTokenSource();
            jeton = _suiteDeLaPage.Token;
        }

        _journal?.LogInformation("World Scoring : bilan de {Jeu} : score {Score}, rang provisoire {Rang}.", bilan.Jeu, bilan.Score, bilan.RangProvisoire);
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), PleinEcran(fin: WorldScoringEcrans.ComposerLeBilan(bilan, T, _glypheValider, _glypheAnnuler)));
        AnnoncerLaPage("ws-recap", true);
        _ = Task.Run(async () =>
        {
            try
            {
                // La fenetre parait sur son fil : la manette se prend un instant apres.
                await Task.Delay(300, jeton).ConfigureAwait(false);
                if (!_overlay.PrendreLeFocus()) _journal?.LogInformation("World Scoring : le bilan n'a pas pu prendre la manette, ES la garde.");
                await Task.Delay(DureeDuBilan, jeton).ConfigureAwait(false);
                FermerLeBilan();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>Ce que le bilan montre, d'apres ce qui a ete lu pendant la partie et le classement du depart.</summary>
    internal static BilanDuScore BilanDe(DepartDuScore d, IReadOnlyList<LeaderboardClient.Ligne> monde, long? score, long? dureeMs,
        (string Statut, int? Rang, long? Score, long? Continues, long? DureeMs)? verdict, string prevol, string deckNom, IReadOnlyList<LigneDuDeck> deck, int? vies)
    {
        var lignes = monde.OrderBy(l => l.Rang).ToList();
        var valeur = verdict?.Score is > 0 ? verdict.Value.Score : score;
        int? rang = null;
        int? rangAuDessus = null;
        long? ecart = null;
        int? classes = null;
        if (valeur is { } v && d.Tableau)
        {
            rang = 1 + lignes.Count(l => l.Valeur > v);
            var dessus = lignes.Where(l => l.Valeur > v && !l.CestMoi).OrderBy(l => l.Valeur).FirstOrDefault();
            if (dessus is not null)
            {
                rangAuDessus = dessus.Rang;
                ecart = dessus.Valeur - v;
            }

            classes = lignes.Count + (lignes.Any(l => l.CestMoi) || v <= 0 ? 0 : 1);
        }

        var tete = lignes.FirstOrDefault();
        return new BilanDuScore(
            d.Cle,
            d.Jeu,
            d.Fanart,
            d.Mode,
            d.Joueur,
            valeur,
            d.Record,
            rang,
            classes,
            rangAuDessus,
            ecart,
            tete?.Valeur,
            tete?.Rang ?? 1,
            verdict?.DureeMs is > 0 ? verdict.Value.DureeMs : dureeMs,
            vies,
            verdict?.Continues,
            verdict?.Statut ?? "",
            verdict?.Statut == "published" ? verdict.Value.Rang : null,
            prevol,
            deckNom,
            deck);
    }

    private static BilanDuScore BilanDe(Suivi s)
    {
        var debut = s.JeuDemarre ?? s.PremierSignal;
        var fin = s.JeuFini ?? DateTime.UtcNow;
        long? duree = debut is { } d ? (long)(fin - d).TotalMilliseconds : null;
        return BilanDe(s.Depart, s.Monde, s.ScoreCertifie ?? s.ScoreLu, duree, s.Verdict, s.Prevol, s.DeckNom, s.Deck, s.ViesPerdues);
    }

    /// <summary>Le bilan a l'ecran se redessine (verdict, stats du Deck) ; sa cle change avec le verdict : le titre se rejoue.</summary>
    private void Recomposer()
    {
        EcranDeFin? fin;
        lock (_verrou)
        {
            if (_page != Page.Bilan || _suivi is not { } suivi) return;
            var bilan = BilanDe(suivi);
            if (bilan == suivi.Bilan) return;
            suivi.Bilan = bilan;
            fin = WorldScoringEcrans.ComposerLeBilan(bilan, T, _glypheValider, _glypheAnnuler);
        }

        _overlay.MontrerMultiplayer(PleinEcran(fin: fin));
    }

    private void FermerLeBilan()
    {
        lock (_verrou)
        {
            if (_page != Page.Bilan) return;
            _page = Page.Aucune;
            _suivi = null;
            _suiteDeLaPage?.Cancel();
        }

        AnnoncerLaPage("ws-recap", false);
        _overlay.Fermer();
        EmulatorForeground.FocusEmulationStation();
    }

    private void RelancerDepuisLeBilan()
    {
        Action? rejouer;
        lock (_verrou)
        {
            if (_page != Page.Bilan) return;
            rejouer = _suivi?.Rejouer;
        }

        FermerLeBilan();
        if (rejouer is null) return;
        _journal?.LogInformation("World Scoring : defi relance depuis le bilan.");
        rejouer();
    }

    // ── Les boutons sur une page ─────────────────────────────────────────────────

    /// <summary>Valider sur une page : au bilan, maintenu il relance, relache court il ferme. Le depart l'ignore.</summary>
    public void ValiderSurLaPage(bool appuye)
    {
        bool maintenu;
        Page page;
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

        if (page != Page.Bilan || garde) return;
        if (maintenu) RelancerDepuisLeBilan();
        else FermerLeBilan();
    }

    /// <summary>Retour sur une page : maintenu pendant le depart, il ferme le jeu ; au bilan, il ferme la page.</summary>
    public void AnnulerSurLaPage(bool appuye)
    {
        bool maintenu;
        Page page;
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

        if (page == Page.Depart && maintenu) QuitterLeDepart();
        else if (page == Page.Bilan && !garde) FermerLeBilan();
    }

    private bool Maintenu(DateTime? depuis)
        => depuis is { } debut && (DateTime.UtcNow - debut).TotalMilliseconds >= Math.Max(200, _options?.CurrentValue.Leaderboard.LongPressMs ?? 600);

    // ── Outils ───────────────────────────────────────────────────────────────────

    /// <summary>La page plein ecran annoncee au PlayerDeck (deck.page) : tant qu'elle est la, il ne montre aucune valeur.</summary>
    private void AnnoncerLaPage(string page, bool ouverte)
    {
        try
        {
            _ = _bus.PublishAsync(new EventEnvelope
            {
                Type = "deck.page",
                Payload = new RetroBat.Api.Deck.DeckPage(page, ouverte, "api", DateTime.UtcNow),
            });
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "World Scoring : page {Page} non annoncee au Deck.", page);
        }
    }

    /// <summary>Les apercus et les essais se dessinent dans une langue choisie, sans reglages d'ES.</summary>
    internal string? LangueDApercu { get; init; }

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

    private static ContenuMultiplayer PleinEcran(EcranDeLancement? lancement = null, EcranDeFin? fin = null)
        => new([string.Empty], 0, false, string.Empty, string.Empty, string.Empty, null, [], -1, string.Empty, string.Empty, [],
            PleinEcran: true, Lancement: lancement, Fin: fin);

    private static JsonElement Racine(object? payload) => payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);

    private static string Texte(JsonElement r, string nom)
        => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Entier(JsonElement r, string nom)
        => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long? Long(JsonElement r, string nom)
        => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
