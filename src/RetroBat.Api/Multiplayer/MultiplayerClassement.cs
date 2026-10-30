namespace RetroBat.Api.Multiplayer;

/// <summary>Le rang d'une place humaine a la fin d'une rencontre : 1 = premier, egalites permises ; son camp s'il y en a.</summary>
public sealed record MultiplayerRang(int Seat, int Rank, int? Camp = null);

/// <summary>
/// L'ISSUE D'UNE RENCONTRE telle que la borne la rapporte (CDC Classements v1.0 §6) : la place gagnante pour les sites et les
/// rapports d'avant (la place seule au rang 1, sinon 0, le match nul), et le rang de chaque humain quand la fin les classe.
/// </summary>
public sealed record MultiplayerIssue(int Gagnante, IReadOnlyList<MultiplayerRang> Rangs)
{
    /// <summary>Un duel lu par <c>end</c> ou <c>match_end</c> : la place gagnante seule (0 pour un match nul).</summary>
    public static MultiplayerIssue DuVainqueur(int gagnante) => new(gagnante, []);

    /// <summary>Des rangs : la place gagnante est celle qui est seule au rang 1 ; plusieurs au rang 1, un match nul.</summary>
    public static MultiplayerIssue DesRangs(IReadOnlyList<MultiplayerRang> rangs)
    {
        var premiers = rangs.Where(r => r.Rank == 1).ToList();
        return new MultiplayerIssue(premiers.Count == 1 ? premiers[0].Seat : MultiplayerResultats.MatchNul, rangs);
    }
}

/// <summary>Une lecture de la memoire du jeu : la garde, le signal de fin, et chaque place (null : pas lu).</summary>
public sealed record MultiplayerReleve(long? Garde, long? Signal, IReadOnlyDictionary<int, MultiplayerReleveDePlace> Places);

/// <summary>Ce qu'on a lu d'une place : sa valeur, l'octet de sa mort et celui de son arrivee (null : pas lu ou absent).</summary>
public sealed record MultiplayerReleveDePlace(long? Valeur, long? Mort = null, long? Fini = null);

/// <summary>
/// LE CLASSEMENT D'UNE RENCONTRE EN COURS (CDC Classements v1.0 §4 et §5), sans lecture : il recoit les releves, dans
/// l'ordre et a leur instant, et rend les rangs des humains quand la rencontre est finie. Toutes les places se lisent,
/// l'ordinateur compris, mais seuls les humains sont classes, entre eux : une victoire de l'ordinateur ne donne la victoire
/// a personne. Dans un jeu en equipes, chaque camp prend le meilleur de ses membres, et chaque humain le rang de son camp.
///
/// Une valeur n'est retenue qu'apres <see cref="MultiplayerRankingEnd.SettleMs"/> sans changer, et seulement sous la garde ;
/// rien ne finit avant d'avoir vu un depart (chaque humain en jeu, les compteurs sous le seuil, le signal de fin retombe) :
/// la demo, ou l'ecran de fin de la rencontre d'avant, ne terminent rien.
/// </summary>
public sealed class MultiplayerClassementEnCours
{
    private readonly MultiplayerRankingEnd _fin;
    private readonly IReadOnlyList<int> _humains;

    /// <summary>Le suivi de chaque valeur : sa derniere lecture et depuis quand elle n'a pas change.</summary>
    private readonly Dictionary<string, (long Valeur, long Depuis)> _suivis = new(StringComparer.Ordinal);

    private readonly HashSet<int> _vusEnJeu = [];
    private bool _signalRetombe;
    private bool _departVu;

    /// <summary>Survie : l'instant ou chaque place est tombee. Arrivee : la position de chaque place a son arrivee.</summary>
    private readonly Dictionary<int, long> _eliminees = [];
    private readonly Dictionary<int, long> _arrivees = [];

    /// <summary>Score fige : le dernier score stable de chaque place en vie, et celui retenu a sa premiere mort.</summary>
    private readonly Dictionary<int, long> _derniersScores = [];
    private readonly Dictionary<int, long?> _retenus = [];

    public MultiplayerClassementEnCours(MultiplayerRankingEnd fin, IReadOnlyCollection<int> humains)
    {
        _fin = fin;
        _humains = humains.Distinct().OrderBy(h => h).ToList();
    }

    /// <summary>Les rangs des humains si la rencontre est finie a ce releve, sinon null.</summary>
    public IReadOnlyList<MultiplayerRang>? Observer(MultiplayerReleve releve, long instantMs)
    {
        if (!_fin.Classable(_humains))
        {
            return null;
        }

        if (_fin.Guard is { } garde)
        {
            if (releve.Garde is not { } lue)
            {
                return null;
            }

            // Hors de la garde (un menu, une transition), rien ne se lit : les valeurs repartent de zero a son retour.
            if (lue != garde.Value)
            {
                _suivis.Clear();
                return null;
            }
        }

        var signalTenu = false;
        if (_fin.Over is { } signal && releve.Signal is { } valeurDuSignal)
        {
            _signalRetombe |= valeurDuSignal != signal.Value;
            signalTenu = Tenue("signal", valeurDuSignal == signal.Value ? 1 : 0, instantMs) == 1 && valeurDuSignal == signal.Value;
        }

        return _fin.Rule switch
        {
            MultiplayerRankingEnd.Manches => Manches(releve, instantMs, signalTenu),
            MultiplayerRankingEnd.Survie => Survie(releve, instantMs, signalTenu),
            MultiplayerRankingEnd.Arrivee => Arrivee(releve, instantMs, signalTenu),
            MultiplayerRankingEnd.ScoreFige => ScoreFige(releve, instantMs, signalTenu),
            _ => null,
        };
    }

    // ── Les quatre regles ──────────────────────────────────────────────────

    /// <summary>Premier a N manches (une place humaine ou non), ou le signal de fin : les humains par leur compteur.</summary>
    private IReadOnlyList<MultiplayerRang>? Manches(MultiplayerReleve releve, long ms, bool signalTenu)
    {
        var tenues = new Dictionary<int, long?>();
        var lues = new Dictionary<int, long?>();
        foreach (var place in _fin.Seats)
        {
            // Tous les bits a 1 : un compteur pas encore initialise (une place que personne ne tient).
            var lue = Lue(releve, place.Seat) is { } v && !(!place.Bcd && !place.Digits && v == (1L << (8 * place.Bytes)) - 1) ? v : (long?)null;
            lues[place.Seat] = lue;
            tenues[place.Seat] = Tenue("v" + place.Seat, lue, ms);
        }

        if (!_departVu)
        {
            _departVu = _humains.All(h => lues.GetValueOrDefault(h) is not null)
                && lues.Values.All(v => v is null || _fin.FirstTo is not { } seuil || v < seuil)
                && (_fin.Over is null || _signalRetombe);
            return null;
        }

        var auSeuil = _fin.FirstTo is { } premierA && tenues.Values.Any(v => v >= premierA);
        if (!auSeuil && !signalTenu)
        {
            return null;
        }

        // Chaque humain doit avoir une valeur retenue : sinon le releve suivant tranchera.
        if (_humains.Any(h => tenues.GetValueOrDefault(h) is null))
        {
            return null;
        }

        return Classer(seat => tenues.GetValueOrDefault(seat) is { } v ? (0, -v) : (1, 0));
    }

    /// <summary>
    /// Le dernier en vie : une place tombe quand sa valeur reste a <c>out_at</c> ou en dessous (ou quand sa mort prend sa
    /// valeur). La rencontre est acquise des qu'il ne reste qu'un humain (ou un camp d'humains) debout, l'ordinateur en jeu
    /// ou non ; le signal de fin classe ceux qui restent ensemble.
    /// </summary>
    private IReadOnlyList<MultiplayerRang>? Survie(MultiplayerReleve releve, long ms, bool signalTenu)
    {
        foreach (var place in _fin.Seats)
        {
            var lu = releve.Places.GetValueOrDefault(place.Seat);
            bool? tombee = null;
            if (place.Dead is { } mort && lu?.Mort is { } octet)
            {
                tombee = octet == mort.Value;
            }

            if (tombee is not true && place.CoreRam.Length > 0 && lu?.Valeur is { } valeur)
            {
                tombee = valeur <= _fin.OutAt;
            }

            if (tombee is false)
            {
                _vusEnJeu.Add(place.Seat);
            }

            var cle = "m" + place.Seat;
            if (Tenue(cle, tombee is null ? null : tombee.Value ? 1 : 0, ms) == 1 && _departVu && !_eliminees.ContainsKey(place.Seat))
            {
                // L'instant ou elle a commence a tomber : deux places tombees dans la meme fenetre partagent leur rang.
                _eliminees[place.Seat] = _suivis[cle].Depuis;
            }
        }

        if (!_departVu)
        {
            _departVu = _humains.All(_vusEnJeu.Contains) && (_fin.Over is null || _signalRetombe);
            return null;
        }

        var debout = Unites().Count(unite => unite.Membres.Any(m => !_eliminees.ContainsKey(m)));
        if (debout > 1 && !signalTenu)
        {
            return null;
        }

        return Classer(seat => _eliminees.TryGetValue(seat, out var t) ? (1, -t) : (0, 0), egales: (a, b) =>
            a.Item1 == b.Item1 && (a.Item1 == 0 || Math.Abs(a.Item2 - b.Item2) <= _fin.SettleMs));
    }

    /// <summary>
    /// L'ordre d'arrivee : la position de chaque place, lue quand son drapeau d'arrivee prend sa valeur (ou pour toutes au
    /// signal de fin). Un humain qui n'a pas fini au signal se classe derriere ceux qui ont fini.
    /// </summary>
    private IReadOnlyList<MultiplayerRang>? Arrivee(MultiplayerReleve releve, long ms, bool signalTenu)
    {
        var positions = new Dictionary<int, long?>();
        foreach (var place in _fin.Seats)
        {
            var lu = releve.Places.GetValueOrDefault(place.Seat);
            positions[place.Seat] = Tenue("v" + place.Seat, lu?.Valeur, ms);
            if (place.Finished is not { } fini || lu?.Fini is not { } octet)
            {
                continue;
            }

            if (octet != fini.Value)
            {
                _vusEnJeu.Add(place.Seat);
            }

            var cle = "f" + place.Seat;
            if (Tenue(cle, octet == fini.Value ? 1 : 0, ms) == 1 && _departVu && !_arrivees.ContainsKey(place.Seat))
            {
                // Sans octet de position (v1.1 §7, n° 4) : l'instant ou son drapeau a commence a tenir fait sa position.
                if (_fin.ParOrdreDArrivee)
                {
                    _arrivees[place.Seat] = _suivis[cle].Depuis;
                }
                else if (positions[place.Seat] is { } position)
                {
                    _arrivees[place.Seat] = position;
                }
            }
        }

        if (!_departVu)
        {
            _departVu = _humains.All(h => _fin.Place(h)?.Finished is null || _vusEnJeu.Contains(h)) && (_fin.Over is null || _signalRetombe);
            return null;
        }

        if (!signalTenu && !_humains.All(_arrivees.ContainsKey))
        {
            return null;
        }

        if (_fin.ParOrdreDArrivee)
        {
            // Deux arrivees dans la meme fenetre partagent leur rang, comme deux chutes en survie ; un humain pas arrive au
            // signal passe derriere, a egalite avec les autres retardataires.
            return Classer(seat => _arrivees.TryGetValue(seat, out var t) ? (0, t) : (1, 0), egales: (a, b) =>
                a.Item1 == b.Item1 && (a.Item1 == 1 || Math.Abs(a.Item2 - b.Item2) <= _fin.SettleMs));
        }

        return Classer(seat => _arrivees.TryGetValue(seat, out var p) ? (0, p) : (1, positions.GetValueOrDefault(seat) ?? long.MaxValue));
    }

    /// <summary>
    /// Le score d'avant la premiere mort (la regle du 1CC : un continue ne compte plus) ; une place encore en vie au signal
    /// de fin garde son score de ce moment. Finie au signal, ou quand chaque humain est mort une fois.
    /// </summary>
    private IReadOnlyList<MultiplayerRang>? ScoreFige(MultiplayerReleve releve, long ms, bool signalTenu)
    {
        foreach (var place in _fin.Seats)
        {
            var lu = releve.Places.GetValueOrDefault(place.Seat);
            bool? morte = place.Dead is { } mort && lu?.Mort is { } octet ? octet == mort.Value : null;
            var score = Tenue("v" + place.Seat, lu?.Valeur, ms);
            if (morte is false)
            {
                _vusEnJeu.Add(place.Seat);
                if (_departVu && !_retenus.ContainsKey(place.Seat) && score is { } s)
                {
                    _derniersScores[place.Seat] = s;
                }
            }

            if (Tenue("m" + place.Seat, morte is null ? null : morte.Value ? 1 : 0, ms) == 1 && _departVu && !_retenus.ContainsKey(place.Seat))
            {
                _retenus[place.Seat] = _derniersScores.TryGetValue(place.Seat, out var retenu) ? retenu : null;
            }
        }

        if (!_departVu)
        {
            _departVu = _humains.All(_vusEnJeu.Contains) && (_fin.Over is null || _signalRetombe);
            return null;
        }

        if (!signalTenu && !_humains.All(_retenus.ContainsKey))
        {
            return null;
        }

        return Classer(seat =>
        {
            var retenu = _retenus.TryGetValue(seat, out var r) ? r : _derniersScores.TryGetValue(seat, out var d) ? d : (long?)null;
            return retenu is { } v ? (0, -v) : (1, 0);
        });
    }

    // ── Le classement ──────────────────────────────────────────────────────

    /// <summary>Les unites classees : chaque humain, ou, en equipes, chaque camp qui a un humain (avec tous ses membres).</summary>
    private IReadOnlyList<(int? Camp, IReadOnlyList<int> Membres, IReadOnlyList<int> Humains)> Unites()
    {
        if (!_fin.AvecCamps)
        {
            return _humains.Select(h => ((int?)null, (IReadOnlyList<int>)[h], (IReadOnlyList<int>)[h])).ToList();
        }

        return _humains.GroupBy(h => _fin.Place(h)?.Camp)
            .Where(g => g.Key is not null)
            .Select(g => (g.Key, (IReadOnlyList<int>)_fin.Seats.Where(s => s.Camp == g.Key).Select(s => s.Seat).ToList(), (IReadOnlyList<int>)g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Les rangs des humains : chaque unite prend la meilleure cle de ses membres (la plus petite), puis le rang de
    /// competition (1, 1, 3) ; deux unites « egales » partagent leur rang.
    /// </summary>
    private IReadOnlyList<MultiplayerRang> Classer(Func<int, (long, long)> cle, Func<(long, long), (long, long), bool>? egales = null)
    {
        egales ??= (a, b) => a == b;
        var unites = Unites().Select(u => (u.Camp, u.Humains, Cle: u.Membres.Select(cle).Min())).OrderBy(u => u.Cle).ToList();
        var rangs = new List<MultiplayerRang>();
        var tete = 0;
        for (var i = 0; i < unites.Count; i++)
        {
            // Un groupe d'egales se mesure a sa tete : trois eliminations a 0,8 s d'ecart ne font pas une chaine d'egalites.
            if (!egales(unites[tete].Cle, unites[i].Cle))
            {
                tete = i;
            }

            rangs.AddRange(unites[i].Humains.Select(h => new MultiplayerRang(h, tete + 1, unites[i].Camp)));
        }

        return rangs.OrderBy(r => r.Rank).ThenBy(r => r.Seat).ToList();
    }

    // ── Les valeurs tenues ─────────────────────────────────────────────────

    private static long? Lue(MultiplayerReleve releve, int place) => releve.Places.GetValueOrDefault(place)?.Valeur;

    /// <summary>
    /// La valeur retenue sous cette cle : la lecture, si elle n'a pas change depuis <see cref="MultiplayerRankingEnd.SettleMs"/>.
    /// Une lecture manquee ne change rien au suivi.
    /// </summary>
    private long? Tenue(string cle, long? lue, long ms)
    {
        if (lue is { } valeur && (!_suivis.TryGetValue(cle, out var suivi) || suivi.Valeur != valeur))
        {
            _suivis[cle] = (valeur, ms);
        }

        return _suivis.TryGetValue(cle, out var tenu) && ms - tenu.Depuis >= _fin.SettleMs ? tenu.Valeur : null;
    }
}
