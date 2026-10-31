using System.Globalization;
using RetroBat.Api.Leaderboard;
using Etat = RetroBat.Api.Leaderboard.LeaderboardOverlayService.EtatAuLancement;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// L'ECRAN DE LANCEMENT, le VS (maquettes validees par le user le 28/10) : ce que chaque borne sait deja, sans rien demander
/// de plus a la plateforme (lot 1).
/// <list type="bullet">
/// <item>du decompte a l'arrivee du jeu, tout le monde charge ;</item>
/// <item>le jeu la, l'hote est pret et ses invites se synchronisent ; chez un invite, l'hote est pret et lui se synchronise ;</item>
/// <item>chez l'hote d'un depart pret, l'ecran reste devant le jeu tant que la boucle du depart le tient (rien ne bouge
/// dessous), et le depart se donne quand le premier invite arrive ; chez un invite, il reste 3 s, le temps de sa connexion ;
/// sans boucle, il s'efface a l'arrivee du jeu, comme avant (il ne cacherait que du jeu) ;</item>
/// <item>« A VOUS DE JOUER ! » 1,3 s, puis l'ecran s'efface en 0,35 s ;</item>
/// <item>au-dela d'une minute d'attente, REESSAYER (A) relance l'attente et QUITTER (B maintenu) ferme la partie.</item>
/// </list>
/// </summary>
public sealed partial class MultiplayerPanelService
{
    private static readonly TimeSpan AttenteTropLongue = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TenueMax = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan TenueDeLInvite = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InvitePretApres = TimeSpan.FromSeconds(2.2);
    private static readonly TimeSpan DureeDuDepart = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan DureeDeLEffacement = TimeSpan.FromMilliseconds(350);

    private readonly Dictionary<int, (Etat Etat, DateTime Depuis)> _etatsAuLancement = new();
    private MultiplayerRoom? _roomDuLancement;
    private bool _tenirLeLancement;
    private bool _jeuArrive;
    private DateTime _jeuArriveA;
    private DateTime _attenteRelanceeA;
    private DateTime? _departDuLancement;
    private CancellationTokenSource? _suiteDuLancement;

    /// <summary>L'ecran de lancement tient-il au-dessus du jeu ? Tant qu'il tient, l'arrivee du jeu ne l'efface pas.</summary>
    public bool TientLeLancement
    {
        get { lock (_verrou) return _mode == Mode.Presentation && _tenirLeLancement && _jeuArrive && _roomDuLancement is not null; }
    }

    /// <summary>Le lancement commence : chaque place charge. <paramref name="tenir"/> : l'ecran restera devant le jeu.</summary>
    private void PreparerLeLancement(MultiplayerRoom room, bool tenir)
    {
        lock (_verrou)
        {
            _suiteDuLancement?.Cancel();
            _suiteDuLancement = null;
            _roomDuLancement = room;
            _tenirLeLancement = tenir;
            _jeuArrive = false;
            _departDuLancement = null;
            _etatsAuLancement.Clear();
            foreach (var joueur in room.Players.Where(j => j.Seat is not null))
            {
                _etatsAuLancement[joueur.Seat!.Value] = (Etat.Chargement, DateTime.UtcNow);
            }
        }
    }

    private void OublierLeLancement()
    {
        lock (_verrou)
        {
            _suiteDuLancement?.Cancel();
            _suiteDuLancement = null;
            _tenirLeLancement = false;
            _jeuArrive = false;
            _roomDuLancement = null;
            _departDuLancement = null;
        }
    }

    private void Noter(int place, Etat etat)
    {
        if (!_etatsAuLancement.TryGetValue(place, out var avant) || avant.Etat != etat)
        {
            _etatsAuLancement[place] = (etat, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Le jeu est a l'ecran. Sans tenue, la presentation s'efface comme avant. Avec, l'ecran passe devant le jeu, les etats
    /// avancent (l'hote pret, ses invites en synchronisation), et le suivi du lancement demarre.
    /// </summary>
    private bool TenirDevantLeJeu()
    {
        MultiplayerRoom? room;
        CancellationToken jeton;
        bool hote;
        lock (_verrou)
        {
            room = _roomDuLancement;
            if (_mode != Mode.Presentation || !_tenirLeLancement || room is null)
            {
                return false;
            }

            _jeuArrive = true;
            _jeuArriveA = _attenteRelanceeA = DateTime.UtcNow;
            var moi = room.Players.FirstOrDefault(j => j.You);
            hote = moi?.Host == true;
            foreach (var joueur in room.Players.Where(j => j.Seat is not null))
            {
                Noter(joueur.Seat!.Value, joueur.Host ? Etat.Pret : Etat.Synchronisation);
            }

            _suiteDuLancement?.Cancel();
            _suiteDuLancement = new CancellationTokenSource();
            jeton = _suiteDuLancement.Token;
        }

        _journal.LogInformation("MULTIPLAYER : l'ecran de lancement reste devant le jeu ({Role})", hote ? "hote, boucle du depart" : "invite");
        _overlay.GarderDevantLeJeu();
        MontrerLeLancement();
        _ = Task.Run(() => SuivreLeLancementAsync(hote, jeton));
        return true;
    }

    /// <summary>
    /// Le suivi pendant la tenue : l'invite se dit pret puis part au bout de 3 s ; l'hote attend le depart que donne la fin
    /// de la boucle (<see cref="DonnerLeDepart"/>), 150 s au plus. Chaque seconde, l'ecran repasse devant et se recompose
    /// (l'attente trop longue).
    /// </summary>
    private async Task SuivreLeLancementAsync(bool hote, CancellationToken jeton)
    {
        try
        {
            while (!jeton.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), jeton).ConfigureAwait(false);
                TimeSpan depuis;
                lock (_verrou)
                {
                    if (_departDuLancement is not null || _mode != Mode.Presentation)
                    {
                        return;
                    }

                    depuis = DateTime.UtcNow - _jeuArriveA;
                    if (!hote && depuis >= InvitePretApres && _roomDuLancement?.Players.FirstOrDefault(j => j.You)?.Seat is { } maPlace)
                    {
                        Noter(maPlace, Etat.Pret);
                    }
                }

                if (!hote && depuis >= TenueDeLInvite)
                {
                    DonnerLeDepart();
                    return;
                }

                if (hote && depuis >= TenueMax)
                {
                    _journal.LogWarning("MULTIPLAYER : l'ecran de lancement tenait depuis {Secondes:F0} s, le depart se donne", depuis.TotalSeconds);
                    DonnerLeDepart();
                    return;
                }

                _overlay.GarderDevantLeJeu();
                MontrerLeLancement();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// LE DEPART : chez l'hote, la boucle du depart pret s'arrete (un invite arrive) ; chez l'invite, sa tenue est finie.
    /// « A VOUS DE JOUER ! », puis l'ecran s'efface. Sans tenue, rien.
    /// </summary>
    public void DonnerLeDepart()
    {
        lock (_verrou)
        {
            if (_mode != Mode.Presentation || !_tenirLeLancement || _departDuLancement is not null || _roomDuLancement is null)
            {
                return;
            }

            _departDuLancement = DateTime.UtcNow;
            foreach (var place in _etatsAuLancement.Keys.ToList())
            {
                Noter(place, Etat.Pret);
            }
        }

        MontrerLeLancement();
        _ = Task.Run(async () =>
        {
            await Task.Delay(DureeDuDepart).ConfigureAwait(false);
            lock (_verrou)
            {
                if (_mode != Mode.Presentation)
                {
                    return;
                }

                _tenirLeLancement = false;
            }

            _overlay.Estomper(DureeDeLEffacement);
            await Task.Delay(DureeDeLEffacement).ConfigureAwait(false);
            lock (_verrou)
            {
                if (_mode != Mode.Presentation)
                {
                    return;
                }
            }

            FermerPourLeJeu();
        });
    }

    /// <summary>L'attente trop longue est-elle a l'ecran (RESSAYER, QUITTER) ?</summary>
    private bool AttenteLongueAffichee()
        => _mode == Mode.Presentation && _tenirLeLancement && _jeuArrive && _departDuLancement is null
            && DateTime.UtcNow - _attenteRelanceeA >= AttenteTropLongue && _etatsAuLancement.Values.Any(e => e.Etat != Etat.Pret);

    /// <summary>A relache pendant l'attente trop longue : on attend encore une minute.</summary>
    private bool ReessayerLeLancement()
    {
        lock (_verrou)
        {
            if (!AttenteLongueAffichee())
            {
                return false;
            }

            _attenteRelanceeA = DateTime.UtcNow;
        }

        _journal.LogInformation("MULTIPLAYER : attente trop longue, le joueur attend encore");
        MontrerLeLancement();
        return true;
    }

    /// <summary>B maintenu pendant l'attente trop longue : la partie se ferme, la suite (resultat, salle) suit son cours.</summary>
    private bool QuitterLeLancement()
    {
        lock (_verrou)
        {
            if (!AttenteLongueAffichee())
            {
                return false;
            }
        }

        _journal.LogInformation("MULTIPLAYER : attente trop longue, le joueur quitte la partie");
        OublierLeLancement();
        FermerPourLeJeu();
        _ = RetroBat.Api.Netplay.RetroArchInvite.FermerAsync();
        return true;
    }

    private void MontrerLeLancement()
    {
        lock (_verrou)
        {
            if (_mode != Mode.Presentation)
            {
                return;
            }

            if (_roomDuLancement is { } salle && _connues.TryGetValue(salle.Room, out var fraiche))
            {
                _roomDuLancement = fraiche;
            }
        }

        if (ComposerLEcranDeLancement() is { } ecran)
        {
            _overlay.MontrerMultiplayer(ContenuDuLancement(ecran));
        }
    }

    private static LeaderboardOverlayService.ContenuMultiplayer ContenuDuLancement(LeaderboardOverlayService.EcranDeLancement ecran)
        => new([string.Empty], 0, false, string.Empty, string.Empty, string.Empty, null, [], -1, string.Empty, string.Empty, [], PleinEcran: true, Lancement: ecran);

    /// <summary>Le mot central du type : VS, EN PISTE ! en course, ON FAIT EQUIPE ! en coop.</summary>
    internal static string CleDuMot(string type, string? famille)
        => famille == "coop" || type == StylesDesEcrans.Coop ? "multiplayer.launch.word.coop"
            : type == StylesDesEcrans.Course ? "multiplayer.launch.word.race"
            : "multiplayer.launch.word.versus";

    /// <summary>Le conseil du jeu dans la langue de la borne : la langue exacte, puis ses deux premieres lettres, puis l'anglais.</summary>
    internal static string ConseilDans(IReadOnlyDictionary<string, string>? conseils, string langue)
    {
        if (conseils is null || conseils.Count == 0)
        {
            return string.Empty;
        }

        var courte = langue.Length >= 2 ? langue[..2] : langue;
        foreach (var cle in new[] { langue, courte, "en" })
        {
            var trouve = conseils.FirstOrDefault(c => string.Equals(c.Key, cle, StringComparison.OrdinalIgnoreCase));
            if (trouve.Value is { Length: > 0 } texte)
            {
                return texte;
            }
        }

        return string.Empty;
    }

    private LeaderboardOverlayService.EcranDeLancement? ComposerLEcranDeLancement()
    {
        MultiplayerRoom? room;
        Dictionary<int, (Etat Etat, DateTime Depuis)> etats;
        DateTime? depart;
        bool longue;
        lock (_verrou)
        {
            room = _roomDuLancement;
            etats = new Dictionary<int, (Etat, DateTime)>(_etatsAuLancement);
            depart = _departDuLancement;
            longue = AttenteLongueAffichee();
        }

        var jeu = _jeu?.Game;
        if (room is null || jeu is null)
        {
            return null;
        }

        var langue = Langue();
        string T(string cle) => _textes.Text(cle, langue);
        string Nombre(string cle, int n) => _textes.Format(cle, langue, ("count", n.ToString(CultureInfo.InvariantCulture)));
        var type = StylesDesEcrans.TypeDe(jeu.EloType, jeu.Family);
        var coop = jeu.Family == "coop";
        var milliers = new NumberFormatInfo { NumberGroupSeparator = " ", NumberGroupSizes = [3] };
        var assis = room.Players.Where(j => j.Seat is not null).GroupBy(j => j.Seat!.Value).Select(g => g.First()).OrderBy(j => j.Seat).ToList();
        string TexteDe(Etat etat) => T(etat switch
        {
            Etat.Pret => "multiplayer.launch.state.ready",
            Etat.Synchronisation => "multiplayer.launch.state.syncing",
            Etat.Deconnecte => "multiplayer.launch.state.lost",
            Etat.EnRoute => "multiplayer.launch.state.late",
            _ => "multiplayer.launch.state.loading",
        });
        var joueurs = assis.Select(j =>
        {
            var place = j.Seat!.Value;
            var (etat, depuis) = etats.TryGetValue(place, out var e) ? e : (Etat.Chargement, DateTime.UtcNow.AddSeconds(-5));
            if (longue && etat != Etat.Pret)
            {
                etat = Etat.EnRoute;
            }

            var cote = j.Rating;
            return new LeaderboardOverlayService.JoueurDeLancement(
                place,
                j.Pseudo.Length > 0 ? j.Pseudo : j.Handle,
                PersonnageDe(j),
                PortraitDeJoueur(j),
                j.Country,
                cote is null ? string.Empty : cote.Lettre,
                cote is null ? string.Empty : Math.Round(cote.Value).ToString("#,0", milliers),
                T("multiplayer.launch.wins"),
                cote?.Wins is { } victoires ? victoires.ToString(CultureInfo.InvariantCulture) : string.Empty,
                T("multiplayer.launch.streak"),
                cote?.Streak is { } serie and > 0 ? (serie == 1 ? T("multiplayer.launch.streak_one") : Nombre("multiplayer.launch.streak_value", serie)) : string.Empty,
                etat,
                TexteDe(etat),
                j.RttMs,
                Barres(j.RttMs),
                depuis,
                CoequipiersDe(j));
        }).ToList();

        var attendus = joueurs.Where(j => j.Etat != Etat.Pret).Select(j => j.Pseudo.ToUpperInvariant()).ToList();
        var legende = attendus.Count == 0
            ? T("multiplayer.launch.all_ready")
            : _textes.Format("multiplayer.launch.waiting", langue, ("players", string.Join(T("multiplayer.launch.and"), attendus)));
        var classique = room.Level != MultiplayerLevels.NelfePlay;
        var entete = jeu.EstClasse ? T("multiplayer.launch.header.ranked")
            : coop ? T("multiplayer.launch.header.coop") + (MultiplayerScoring.Concourt(jeu) ? " · " + MultiplayerScoring.Etiquette : string.Empty)
            : T("multiplayer.launch.header.match");
        var sousTitre = type == StylesDesEcrans.Course ? Nombre("multiplayer.launch.race", joueurs.Count)
            : !coop && joueurs.Count == 2 ? T("multiplayer.launch.duel")
            : Nombre("multiplayer.launch.players", joueurs.Count);
        var etiquette = !coop && jeu.End is { FirstTo: >= 2 } fin && type is StylesDesEcrans.Combat or StylesDesEcrans.Puzzle or StylesDesEcrans.Sport or StylesDesEcrans.Tir
            ? Nombre("multiplayer.launch.rounds", fin.FirstTo)
            : string.Empty;
        var conseil = classique ? T("multiplayer.classic.message") : ConseilDans(jeu.Ecran?.Conseils, langue);
        var titreDuConseil = conseil.Length == 0 ? string.Empty : classique ? T("multiplayer.badge.classic") : T("multiplayer.launch.tip");
        return new LeaderboardOverlayService.EcranDeLancement(
            room.Room,
            type,
            T(CleDuMot(type, jeu.Family)),
            jeu.Name,
            entete,
            sousTitre,
            FanartDuJeu(),
            joueurs,
            etiquette,
            titreDuConseil,
            conseil,
            legende,
            [T("multiplayer.launch.step.gathered"), T("multiplayer.launch.step.sync"), T("multiplayer.launch.step.go")],
            attendus.Count == 0 ? 2 : 1,
            T("multiplayer.launch.go"),
            depart,
            longue,
            T("multiplayer.launch.retry"),
            T("multiplayer.launch.quit"),
            T("multiplayer.end.hold"),
            _glypheValider,
            _glypheAnnuler,
            jeu.Ecran?.Police is { Length: > 0 } police ? police : null);
    }
}
