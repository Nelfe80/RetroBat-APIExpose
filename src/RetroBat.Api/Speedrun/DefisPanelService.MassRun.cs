using System.Globalization;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.MassRun;
using static RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Speedrun;

/// <summary>Ce qu'est devenue la course du joueur, d'apres le coeur.</summary>
public enum IssueDeCourse
{
    /// <summary>La ligne franchie.</summary>
    Arrive,

    /// <summary>MASS SURVIVAL : encore en vie a la fin du temps de la salle.</summary>
    Survivant,

    /// <summary>MASS SURVIVAL : la premiere mort.</summary>
    Elimine,

    /// <summary>Le temps de la salle passe sans arrivee.</summary>
    TempsEcoule,

    /// <summary>Le jeu ferme en pleine course.</summary>
    Abandon,
}

/// <summary>
/// Le bilan d'une course MASS RUN. <c>Images</c> : le temps du joueur (arrivee ou elimination). <c>Classement</c> : les lignes
/// du noeud, completees tant que la course dure ; <c>CourseFinie</c> : le temps de la salle est passe, plus rien ne bouge.
/// </summary>
public sealed record BilanDeCourse(MassRunService.SalleOuverte Salle, string Jeu, string Joueur, string? Fanart, IssueDeCourse Issue, uint? Images,
    int? Rang, int Inscrits, int Arrives, IReadOnlyList<MassRun.MassRunCanal.LigneDeClassement> Classement, bool CourseFinie);

/// <summary>
/// LES PAGES PLEIN ECRAN D'UNE COURSE MASS RUN (demande du user du 04/11 : « les ecrans start et stats comme dans
/// multiplayer »), sur les patrons des pages d'une epreuve :
/// <list type="bullet">
/// <item>le depart reprend l'ecran de lancement : la fiche de la salle tient devant le jeu jusqu'a la salle d'attente du
/// coeur, puis « EN PISTE ! » et l'effacement ; B maintenu ferme le jeu ;</item>
/// <item>le bilan reprend l'ecran de fin a la fermeture du jeu : temps, place, coureurs, classement autour du joueur, relu
/// tant que la course dure. A maintenu rejoint la course suivante, A ou B ferme.</item>
/// </list>
/// Seulement pour une salle rejointe depuis le panneau (une salle d'essai ne montre aucune page).
/// </summary>
public sealed partial class DefisPanelService
{
    /// <summary>Le mot central : il ne se traduit pas, comme le nom du mode.</summary>
    internal const string MotMassRun = "MASS RUN";

    private const double ImagesParSecondeNes = 60.0988;

    private sealed record CourseRejointe(MassRunService.SalleOuverte Salle, string Jeu, string? Fanart, DateTime LanceALocal);

    private CourseRejointe? _course;
    private BilanDeCourse? _bilanDeCourse;

    private static string FormatDe(MassRunService.SalleOuverte s) => s.Mort == "elimination" ? "Mass Survival" : "Mass Speedrun";

    private string RegleDe(MassRunService.SalleOuverte s) => s.Mort switch
    {
        "elimination" => T("massrun.panel.death.elimination", "first death eliminates"),
        "jeu" => T("massrun.panel.death.game", "game rules on death"),
        _ => T("massrun.panel.death.start", "death sends back to the start"),
    };

    private static string Horloge(TimeSpan duree)
    {
        if (duree < TimeSpan.Zero) duree = TimeSpan.Zero;
        return $"{(int)duree.TotalMinutes}:{duree.Seconds:00}";
    }

    /// <summary>(13/11) Le temps a la cadence du jeu de la salle (Mega Drive : 59,92 images/s), NES par defaut.</summary>
    private static string TempsDeCourse(uint images, string jeu)
        => Chronometrage.Afficher(images, RetroBat.Api.MassRun.JeuxMassRun.Trouver(jeu)?.ImagesParSeconde ?? ImagesParSecondeNes);

    // ── Le depart ────────────────────────────────────────────────────────────────

    /// <summary><paramref name="salleDAttente"/> : le coeur est en salle d'attente ; <paramref name="depart"/> : « EN PISTE ! ».</summary>
    internal EcranDeLancement ComposerLeDepartMassRun(MassRunService.SalleOuverte s, string jeu, string? fanart, bool salleDAttente, int inscrits,
        DateTime maintenant, DateTime? depart = null)
    {
        var avantLeGo = s.GoUtc - maintenant;
        return new EcranDeLancement(
            "massrun-" + s.Id.ToString(CultureInfo.InvariantCulture),
            StylesDesEcrans.Course,
            MotMassRun,
            jeu,
            T("massrun.page.public", "PUBLIC RACE"),
            FormatDe(s) + " · " + T("massrun.panel.level", "Level") + " " + RetroBat.Api.MassRun.JeuxMassRun.NomDuNiveau(s.Niveau),
            fanart,
            [],
            "",
            "",
            "",
            // Le noeud ne diffuse qu'a partir de deux coureurs (essai du 04/11) : seul, le joueur doit savoir pourquoi il ne voit personne.
            inscrits <= 1 ? T("massrun.page.alone", "You're alone in this room for now: other runners show up when they join") : T("massrun.page.loading", "The game is loading…"),
            [
                T("massrun.page.step.joined", "SPOT TAKEN"),
                T("massrun.page.step.loading", "LOADING THE GAME"),
                T("massrun.page.step.waiting", "WAITING ROOM"),
            ],
            salleDAttente || depart is not null ? 2 : 1,
            T("massrun.page.go", "ON TRACK!"),
            depart,
            false,
            "",
            T("speedrun.page.quit", "QUIT"),
            T("speedrun.page.hold", "HOLD"),
            _glypheValider,
            _glypheAnnuler,
            SortiePossible: true,
            Fiche:
            [
                new(T("massrun.page.format", "Format"), FormatDe(s)),
                new(T("massrun.panel.level", "Level"), RetroBat.Api.MassRun.JeuxMassRun.NomDuNiveau(s.Niveau)),
                new(T("massrun.page.death", "Death"), RegleDe(s)),
                new(T("massrun.panel.registered", "Runners registered"), inscrits.ToString(CultureInfo.InvariantCulture)),
                new(T("massrun.panel.start_in", "Start in"), Horloge(avantLeGo)),
                new(T("massrun.page.limit", "Time limit"), Horloge(TimeSpan.FromSeconds(s.Limite))),
            ],
            Logo: LogoDesPages);
    }

    private void MontrerLeDepartMassRun(CourseRejointe course)
    {
        CancellationToken jeton;
        lock (_verrou)
        {
            _modele.Fermer();
            _page = PageDEpreuve.Preparation;
            _epreuve = null;
            _course = course;
            _bilanAffiche = null;
            _bilanDeCourse = null;
            _validerDepuis = _annulerDepuis = null;
            _suiteDeLaPage?.Cancel();
            _suiteDeLaPage = new CancellationTokenSource();
            jeton = _suiteDeLaPage.Token;
        }

        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal),
            PleinEcran(lancement: ComposerLeDepartMassRun(course.Salle, course.Jeu, course.Fanart, false, course.Salle.Inscrits, DateTime.UtcNow)));
        _ = Task.Run(() => TenirLeDepartMassRunAsync(course, jeton));
    }

    /// <summary>
    /// Devant le jeu jusqu'a la salle d'attente du coeur (150 s au plus), en repassant devant chaque demi-seconde ; le nombre
    /// d'inscrits et le decompte suivent. Puis « EN PISTE ! » 1,3 s, et l'effacement.
    /// </summary>
    private async Task TenirLeDepartMassRunAsync(CourseRejointe course, CancellationToken jeton)
    {
        try
        {
            var limite = DateTime.UtcNow + TenueMax;
            var inscrits = course.Salle.Inscrits;
            var relue = DateTime.MinValue;
            var arrive = false;
            while (DateTime.UtcNow < limite)
            {
                await Task.Delay(500, jeton).ConfigureAwait(false);
                if (_massrun?.Salle is { Salle: var id, Mode: >= 2 } && id == course.Salle.Id)
                {
                    arrive = true;
                    break;
                }

                if (_massrun is not null && DateTime.UtcNow - relue > TimeSpan.FromSeconds(3))
                {
                    relue = DateTime.UtcNow;
                    var salles = await _massrun.LireLesSallesAsync(jeton).ConfigureAwait(false);
                    if (salles.FirstOrDefault(s => s.Id == course.Salle.Id) is { } fraiche) inscrits = fraiche.Inscrits;
                }

                lock (_verrou)
                {
                    if (_page != PageDEpreuve.Preparation || !ReferenceEquals(_course, course)) return;
                }

                _overlay.GarderDevantLeJeu();
                _overlay.MontrerMultiplayer(PleinEcran(lancement: ComposerLeDepartMassRun(course.Salle, course.Jeu, course.Fanart, false, inscrits, DateTime.UtcNow)));
            }

            if (!arrive) _journal?.LogWarning("MASS RUN : le jeu n'est pas arrive en salle d'attente en {Secondes:F0} s, le depart s'efface.", TenueMax.TotalSeconds);
            lock (_verrou)
            {
                if (_page != PageDEpreuve.Preparation || !ReferenceEquals(_course, course)) return;
            }

            _overlay.GarderDevantLeJeu();
            _overlay.MontrerMultiplayer(PleinEcran(lancement: ComposerLeDepartMassRun(course.Salle, course.Jeu, course.Fanart, true, inscrits, DateTime.UtcNow, DateTime.UtcNow)));
            await Task.Delay(DureeDuDepart, jeton).ConfigureAwait(false);
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

    // ── Le bilan ─────────────────────────────────────────────────────────────────

    /// <summary>L'issue de la course d'apres le coeur : le mode le plus avance, l'arrivee, l'elimination. Null : la course n'a pas eu lieu.</summary>
    internal static IssueDeCourse? IssueDe(MassRunService.SalleLocale salle)
    {
        if (salle.Arrivee > 0) return salle.Elimine ? IssueDeCourse.Elimine : IssueDeCourse.Arrive;
        if (salle.ModeMax >= 5 || salle.TempsEcoule) return salle.Survie ? IssueDeCourse.Survivant : IssueDeCourse.TempsEcoule;
        if (salle.ModeMax >= 3) return IssueDeCourse.Abandon;
        return null;
    }

    /// <summary>La salle du reseau est quittee : le bilan, si elle a ete rejointe depuis le panneau et que la course a eu lieu.</summary>
    private void SurSalleQuittee(MassRunService.SalleLocale salle)
    {
        CourseRejointe? course;
        bool preparation;
        lock (_verrou)
        {
            course = _course;
            preparation = _page == PageDEpreuve.Preparation;
        }

        if (course is null || course.Salle.Id != salle.Salle) return;
        if (preparation)
        {
            // Le jeu s'est ferme avant la salle d'attente : le depart ne tient plus rien.
            lock (_verrou)
            {
                _page = PageDEpreuve.Aucune;
                _suiteDeLaPage?.Cancel();
            }

            _overlay.Fermer();
        }

        if (IssueDe(salle) is not { } issue)
        {
            _journal?.LogInformation("MASS RUN : salle {Salle} quittee avant le GO!, pas de bilan.", salle.Salle);
            lock (_verrou)
            {
                if (ReferenceEquals(_course, course)) _course = null;
            }

            EmulatorForeground.FocusEmulationStation();
            return;
        }

        var joueur = Pseudo.Length > 0 ? Pseudo : salle.Pseudo;
        var images = issue is IssueDeCourse.Arrive or IssueDeCourse.Elimine ? salle.Arrivee : (uint?)null;
        var bilan = new BilanDeCourse(course.Salle, course.Jeu, joueur, course.Fanart, issue, images, null, course.Salle.Inscrits, 0, [], false);
        _ = Task.Run(async () =>
        {
            // ES reprend l'ecran quand l'emulateur s'en va : le bilan passe devant lui, pas l'inverse.
            await Task.Delay(400).ConfigureAwait(false);
            OuvrirLeBilanDeCourse(bilan, salle);
        });
    }

    internal EcranDeFin ComposerLeBilanDeCourse(BilanDeCourse b)
    {
        var premier = b.Rang == 1 && b.Issue == IssueDeCourse.Arrive;
        var mesures = new List<MesureDeFin>
        {
            new("chevrons", T("massrun.page.runners", "RUNNERS"), b.Inscrits, 1, false),
            new("chrono", T("massrun.page.finished", "FINISHED"), b.Arrives, b.Inscrits > 0 ? Math.Min(1, (double)b.Arrives / b.Inscrits) : 0, false),
        };
        if (b.Rang is { } rang)
        {
            var part = b.Inscrits > 1 ? 1 - (rang - 1d) / (b.Inscrits - 1) : 1;
            mesures.Add(new MesureDeFin("trophee", T("massrun.page.rank", "RANK"), rang, Math.Clamp(part, 0, 1), premier));
        }

        var valeur = b.Images is { } images ? TempsDeCourse(images, b.Salle.Jeu) : SansTemps;
        var ruban = MotMassRun + (b.Rang is { } place ? " · #" + place.ToString(CultureInfo.InvariantCulture) : "");
        var tete = b.Classement.FirstOrDefault(l => !l.Elimine);
        var ecart = b is { Issue: IssueDeCourse.Arrive, Images: { } t } && tete is not null && t > tete.Images
            ? "+" + TempsDeCourse(t - tete.Images, b.Salle.Jeu) + " " + T("massrun.page.from_first", "from the winner")
            : "";
        var note = b.Inscrits <= 1
            ? T("massrun.page.alone_end", "You were the only runner in this room")
            : b.CourseFinie
            ? FormatDe(b.Salle) + " · " + T("massrun.panel.level", "Level") + " " + RetroBat.Api.MassRun.JeuxMassRun.NomDuNiveau(b.Salle.Niveau)
            : T("massrun.page.still_running", "Run still running: the ranking fills in as runners finish");
        return new EcranDeFin(
            // L'issue et le rang changent la cle : le titre se rejoue quand la place arrive.
            "massrun-bilan-" + b.Salle.Id.ToString(CultureInfo.InvariantCulture) + "-" + b.Issue + "-" + (b.Rang?.ToString(CultureInfo.InvariantCulture) ?? ""),
            StylesDesEcrans.Course,
            TitreDeCourse(b.Issue, premier),
            b.Jeu,
            "",
            b.Fanart,
            [new JoueurDeFin(1, b.Joueur, "", null, "", "", "", false, b.Rang, "", mesures)],
            null,
            note,
            "",
            new TextesDeFin(T("massrun.page.over", "RACE OVER"), "", "", T("speedrun.page.hold", "HOLD"), T("massrun.page.next", "NEXT RACE"),
                T("speedrun.page.quit", "QUIT")),
            _glypheValider,
            _glypheAnnuler,
            MonScore: new ScoreDeFin(FormatDe(b.Salle) + " · " + RetroBat.Api.MassRun.JeuxMassRun.NomDuNiveau(b.Salle.Niveau), valeur, ruban, ecart, premier),
            Logo: LogoDesPages,
            Classement: AutourDuJoueur(b.Classement)
                .Select(l => new LigneDeDefi($"{l.Rang}   {l.Pseudo}", "", l.Elimine ? "✕ " + TempsDeCourse(l.Images, b.Salle.Jeu) : TempsDeCourse(l.Images, b.Salle.Jeu),
                    Marque: l.Moi ? "moi" : ""))
                .ToList());
    }

    /// <summary>Le premier, puis le joueur et ses voisins (5 lignes en tout).</summary>
    internal static IReadOnlyList<MassRun.MassRunCanal.LigneDeClassement> AutourDuJoueur(IReadOnlyList<MassRun.MassRunCanal.LigneDeClassement> lignes, int nombre = 5)
    {
        if (lignes.Count <= nombre) return lignes;
        var moi = lignes.ToList().FindIndex(l => l.Moi);
        var fenetre = nombre - 1;
        var debut = Math.Clamp((moi < 0 ? 0 : moi) - fenetre / 2, 1, lignes.Count - fenetre);
        return [lignes[0], .. lignes.Skip(debut).Take(fenetre)];
    }

    private string TitreDeCourse(IssueDeCourse issue, bool premier) => issue switch
    {
        IssueDeCourse.Arrive when premier => T("massrun.page.title.winner", "WINNER!"),
        IssueDeCourse.Arrive => T("massrun.page.title.finished", "FINISHED!"),
        IssueDeCourse.Survivant => T("massrun.page.title.survivor", "SURVIVOR!"),
        IssueDeCourse.Elimine => T("massrun.page.title.eliminated", "ELIMINATED"),
        IssueDeCourse.TempsEcoule => T("massrun.page.title.time_up", "TIME UP"),
        _ => T("massrun.page.title.quit", "RACE LEFT"),
    };

    /// <summary>Le bilan a l'ecran, puis relu chaque 2 s tant que la course dure : les arrivees remplissent le classement.</summary>
    private void OuvrirLeBilanDeCourse(BilanDeCourse b, MassRunService.SalleLocale salle)
    {
        CancellationToken jeton;
        lock (_verrou)
        {
            _page = PageDEpreuve.Bilan;
            _bilanDeCourse = b;
            _bilanAffiche = null;
            _bilanDepuis = DateTime.UtcNow;
            _validerDepuis = _annulerDepuis = null;
            _suiteDeLaPage?.Cancel();
            _suiteDeLaPage = new CancellationTokenSource();
            jeton = _suiteDeLaPage.Token;
        }

        _journal?.LogInformation("MASS RUN : bilan de la salle {Salle} : {Issue}{Temps}.", b.Salle.Id, b.Issue,
            b.Images is { } i ? " en " + TempsDeCourse(i, b.Salle.Jeu) : "");
        _overlay.OuvrirMultiplayer(EsMenuStyle.Lire(_journal), PleinEcran(fin: ComposerLeBilanDeCourse(b)));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, jeton).ConfigureAwait(false);
                if (!_overlay.PrendreLeFocus()) _journal?.LogInformation("MASS RUN : le bilan n'a pas pu prendre la manette, ES la garde.");
                var fermeture = DateTime.UtcNow + DureeDuBilan;
                var finDeCourse = b.Salle.GoUtc.AddSeconds(b.Salle.Limite + 5);
                var courant = b;
                while (DateTime.UtcNow < fermeture)
                {
                    if (salle.Noeud is { } noeud && !courant.CourseFinie
                        && await ClientDeSalle.LireLeClassementAsync(noeud, salle.Salle, salle.Place, jeton).ConfigureAwait(false) is { } etat)
                    {
                        var finie = DateTime.UtcNow > finDeCourse || etat.Arrives >= etat.Inscrits;
                        var suivant = courant with
                        {
                            Classement = etat.Lignes,
                            Rang = etat.MonRang > 0 ? etat.MonRang : null,
                            Inscrits = etat.Inscrits,
                            Arrives = etat.Arrives,
                            CourseFinie = finie,
                        };
                        lock (_verrou)
                        {
                            if (_page != PageDEpreuve.Bilan || !ReferenceEquals(_bilanDeCourse, courant)) return;
                            _bilanDeCourse = suivant;
                        }

                        if (suivant != courant) _overlay.MontrerMultiplayer(PleinEcran(fin: ComposerLeBilanDeCourse(suivant)));
                        courant = suivant;
                        // Tant que la course dure, le bilan reste : les autres arrivent encore.
                        if (!finie && fermeture < finDeCourse) fermeture = finDeCourse;
                    }

                    await Task.Delay(2000, jeton).ConfigureAwait(false);
                }

                FermerLeBilan();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>A maintenu au bilan : la course suivante, si une salle de ce jeu prend encore des inscriptions.</summary>
    private void CourseSuivante()
    {
        CourseRejointe? course;
        lock (_verrou)
        {
            if (_page != PageDEpreuve.Bilan || _course is null) return;
            course = _course;
            _page = PageDEpreuve.Aucune;
            _bilanDeCourse = null;
            _course = null;
            _suiteDeLaPage?.Cancel();
        }

        _overlay.Fermer();
        _ = Task.Run(async () =>
        {
            var maintenant = DateTime.UtcNow;
            IReadOnlyList<MassRunService.SalleOuverte> salles = [];
            if (_massrun is not null) salles = await _massrun.LireLesSallesAsync(CancellationToken.None).ConfigureAwait(false);
            var suivante = salles.Where(s => s.Jeu == course.Salle.Jeu && s.Inscriptions(maintenant.AddSeconds(2)))
                .OrderBy(s => s.InscriptionsUtc).FirstOrDefault();
            if (suivante is null)
            {
                _journal?.LogInformation("MASS RUN : aucune salle ouverte pour la course suivante.");
                EmulatorForeground.FocusEmulationStation();
                return;
            }

            await RejoindreAsync(suivante, course.Jeu, course.Fanart).ConfigureAwait(false);
        });
    }

    /// <summary>La borne prend sa place dans la salle, lance le jeu et montre le depart ; rend la raison d'un refus, ou null.</summary>
    private async Task<string?> RejoindreAsync(MassRunService.SalleOuverte salle, string jeu, string? fanart)
    {
        if (_massrun is null) return "no_service";
        try
        {
            var lanceA = DateTime.Now;
            await _massrun.RejoindreAsync(_massrun.NoeudCourant, salle.Id, true, CancellationToken.None).ConfigureAwait(false);
            // Comme une epreuve : la musique d'ES se tait jusqu'a la fin du jeu (SonDEmulationStation la rend ensuite).
            _sonDEs?.Couper();
            MontrerLeDepartMassRun(new CourseRejointe(salle, jeu, fanart, lanceA));
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException
            or System.Text.Json.JsonException or KeyNotFoundException)
        {
            _journal?.LogWarning("MASS RUN : jonction refusee : {Message}", e.Message);
            EmulatorForeground.FocusEmulationStation();
            return e.Message;
        }
    }
}
