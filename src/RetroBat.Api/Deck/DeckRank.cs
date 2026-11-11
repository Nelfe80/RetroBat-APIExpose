using System.Text.Json;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Deck;

/// <summary>
/// LE RANG EN DIRECT DU PLAYERDECK (CDC PlayerDeck H-D21) : la place que vaudrait la partie en cours dans le classement
/// NelfePlay du jeu et du mode joues, la valeur du rang au-dessus et l'ecart, le record personnel.
///
/// QUAND : a chaque lancement avec Deck (boutons speciaux des panneaux, <see cref="DeckLaunchService.Preparer"/>), le
/// classement du jeu et du mode est lu UNE fois, avant le jeu, par les clients deja employes par les panneaux
/// (<see cref="LeaderboardClient"/> pour le classement d'un jeu, <see cref="ClassementsDesDefis"/> pour celui d'une
/// epreuve), avec leur cache et leur dernier classement connu. Ensuite, aucun appel reseau : le rang est recalcule en
/// local a chaque valeur classee (<c>score.live.changed</c> du joueur, ou temps d'arrivee d'une epreuve) et publie
/// (<c>deck.rank</c>) quand il change.
///
/// LA REGLE DU RANG EST CELLE DU SITE (rang annonce a la borne, depuis le 31/10) : une ligne par compte et par monde, le
/// rang vaut 1 + le nombre de lignes qui font STRICTEMENT mieux, la ligne du joueur comprise ; une egalite partage donc
/// le rang. Les valeurs sont des nombres (jamais comparees en texte).
///
/// PROVISOIRE, TOUJOURS : seul le score certifie entre au classement. Hors ligne sans classement connu, ou sans
/// classement pour ce mode, le rang n'est pas invente (<c>State</c> offline, no_board) ; classement vide : premier score.
/// Un classement au temps ne se compare pas en cours de course (le chrono monte) : pendant la course, le temps a battre
/// du rang vise et le record perso ; le rang se fixe a l'arrivee.
///
/// LA CERTIFICATION NE CHANGE PAS : ce service lit des evenements et un classement public ; il n'ecrit rien dans la
/// partie, ni dans le passeport, ni dans le prevol, ni dans les verrous.
/// </summary>
public sealed class DeckRankService : IHostedService, IDisposable
{
    /// <summary>Le classement d'une epreuve est garde 60 s par son client : au-dela, une copie est perimee.</summary>
    private static readonly TimeSpan FraicheurDesEpreuves = TimeSpan.FromSeconds(90);

    /// <summary>Comme le Deck du lancement : un armement plus vieux ne vaut plus pour le jeu qui demarre.</summary>
    private static readonly TimeSpan DureeDeLArmement = TimeSpan.FromMinutes(2);

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly LeaderboardClient? _classements;
    private readonly ClassementsDesDefis? _epreuves;
    private readonly NelfePlayScoringCollectionSyncService? _collection;
    private readonly NelfePlayScoringSessionService? _session;
    private readonly DeckStateService? _etat;
    private readonly ILogger<DeckRankService>? _journal;
    private readonly object _verrou = new();
    private IDisposable? _abonnement;
    private Armement? _arme;
    private DeckRank? _publie;
    private long _revision;

    /// <summary>Couture des essais : la lecture du classement (sans reseau).</summary>
    internal Func<DeckRankBoard, string, CancellationToken, Task<DeckRankTable>>? Lecteur { get; set; }

    /// <summary>La lecture du classement du dernier armement (pour les essais).</summary>
    internal Task? Chargement { get; private set; }

    public DeckRankService(IEventBus bus, ApiContext contexte, LeaderboardClient? classements = null, ClassementsDesDefis? epreuves = null,
        NelfePlayScoringCollectionSyncService? collection = null, NelfePlayScoringSessionService? session = null,
        DeckStateService? etat = null, ILogger<DeckRankService>? journal = null)
    {
        _bus = bus;
        _contexte = contexte;
        _classements = classements;
        _epreuves = epreuves;
        _collection = collection;
        _session = session;
        _etat = etat;
        _journal = journal;
        if (_etat is not null) _etat.Rang = () => Courant;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _abonnement = _bus.Subscribe<EventEnvelope>(Recevoir);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _abonnement = null;
        lock (_verrou) _arme?.Arret.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _abonnement?.Dispose();

    /// <summary>Le dernier rang publie pour la partie en cours (repris dans deck.snapshot) ; null hors d'une partie avec Deck.</summary>
    public DeckRank? Courant
    {
        get { lock (_verrou) return _arme is null ? null : _publie; }
    }

    // ─────────────────────────────── armement, au lancement ───────────────────────────────

    /// <summary>
    /// Un lancement avec Deck se prepare : quel classement, et sa lecture (en fond, jamais bloquante). Sans Deck pour ce jeu,
    /// rien. Appele par <see cref="DeckLaunchService"/>.
    /// </summary>
    public void Armer(DeckLancement lancement)
    {
        if (lancement.Choices.Count == 0)
        {
            // un jeu sans Deck : un armement reste d'un lancement precedent ne doit pas suivre ce jeu-ci
            lock (_verrou)
            {
                _arme?.Arret.Cancel();
                _arme = null;
                _publie = null;
            }

            return;
        }

        var classement = lancement.Board ?? ClassementParDefaut(lancement);
        var arme = new Armement(lancement, classement, DateTime.UtcNow, Monde());
        lock (_verrou)
        {
            _arme?.Arret.Cancel();
            _arme = arme;
            _publie = null;
        }

        Calculer(arme);
        if (classement is null)
        {
            _journal?.LogInformation("PlayerDeck : rang sans classement pour {Jeu} ({Mode}).", lancement.Game, lancement.Mode);
            return;
        }

        Chargement = Task.Run(() => ChargerAsync(arme));
    }

    /// <summary>
    /// Le classement quand l'appelant ne l'a pas dit : World Scoring, la regle principale du jeu (celle du cartouche du
    /// defi) ; MULTIPLAYER, son 1CC MULTI s'il en a un. Null : aucun classement pour ce mode.
    /// </summary>
    internal DeckRankBoard? ClassementParDefaut(DeckLancement l)
    {
        var groupe = GroupeDe(l);
        if (groupe.Length == 0) return null;
        var regles = _collection?.ReglesOuvertes(groupe) ?? [];
        switch (l.Mode)
        {
            case DeckModes.WorldScoring:
                return DeckRankBoard.DuJeu(groupe, LeaderboardClient.ReglePrincipale(regles), regles);
            case DeckModes.Multiplayer:
                var multi = regles.FirstOrDefault(r => r.Equals(NelfePlayScoringReporter.CategorieMulti, StringComparison.OrdinalIgnoreCase))
                    ?? regles.FirstOrDefault(r => LeaderboardInputService.FamilleEtMode(r).Famille == NelfePlayScoringReporter.CategorieMulti);
                return multi is null ? null : DeckRankBoard.DuJeu(groupe, multi, regles, l.Seat);
            default:
                return null;
        }
    }

    private static string GroupeDe(DeckLancement l)
    {
        if (l.RomGroup is { Length: > 0 } g) return g;
        var barre = l.Game.IndexOf('/');
        return barre >= 0 ? l.Game[(barre + 1)..] : l.Game;
    }

    /// <summary>Le monde de la partie : celui de la session de salle ou de contest, sinon la maison.</summary>
    private string Monde() => _session?.Get()?.World is { Length: > 0 } w ? w : "home";

    private async Task ChargerAsync(Armement arme)
    {
        DeckRankTable table;
        try
        {
            table = await (Lecteur ?? LireAsync).Invoke(arme.Classement!, arme.Lancement.Player, arme.Arret.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _journal?.LogInformation(ex, "PlayerDeck : classement de {Jeu} illisible, rang hors ligne.", arme.Lancement.Game);
            table = DeckRankTable.HorsLigne;
        }

        lock (_verrou)
        {
            if (!ReferenceEquals(_arme, arme)) return;
            arme.Table = table;
        }

        _journal?.LogInformation("PlayerDeck : classement {Regle} de {Jeu} lu pour le rang ({Lignes} lignes, {Source}).",
            arme.Classement!.Ruleset, arme.Lancement.Game, table.Rows.Count, table.Source);
        Calculer(arme);
    }

    /// <summary>La lecture reelle : les clients des panneaux, avec leur cache (rien de plus n'est demande au site).</summary>
    private async Task<DeckRankTable> LireAsync(DeckRankBoard classement, string joueur, CancellationToken ct)
    {
        if (classement.Board == DeckRankBoard.Epreuve)
        {
            if (_epreuves is null) return DeckRankTable.HorsLigne;
            await _epreuves.ChargerAsync([classement.Ruleset], ct).ConfigureAwait(false);
            var (epreuve, lu) = _epreuves.Dernier(classement.Ruleset);
            if (epreuve is null) return DeckRankTable.HorsLigne;
            var lignes = epreuve.Lignes.Select(l => new DeckRankRow(l.Rang, l.Joueur, l.Valeur, "", EstMoi(l.Joueur, "", joueur))).ToList();
            var frais = lu is { } quand && DateTime.UtcNow - quand <= FraicheurDesEpreuves;
            return new DeckRankTable(lignes, frais ? DeckRankTable.Frais : DeckRankTable.Perime, lu, lignes.Count < epreuve.Total, epreuve.Total, null);
        }

        if (_classements is null) return DeckRankTable.HorsLigne;
        var resultat = await _classements.MondeAsync(classement.RomGroup, joueur, ct, classement.Ruleset).ConfigureAwait(false);
        var rangees = resultat.Lignes
            .Select(l => new DeckRankRow(l.Rang, l.Joueur, l.Valeur, l.Monde.Length > 0 ? l.Monde : "home", EstMoi(l.Joueur, l.Poignee, joueur)))
            .ToList();
        bool? plusBas = resultat.Lignes.Count > 0 ? resultat.Lignes[0].PlusBasEstMieux : null;
        var source = resultat.Etat == LeaderboardClient.EtatHorsLigne
            ? rangees.Count > 0 ? DeckRankTable.Perime : DeckRankTable.HorsLigneSource
            : DeckRankTable.Frais;
        return new DeckRankTable(rangees, source, source == DeckRankTable.Frais ? DateTime.UtcNow : null,
            rangees.Count >= LeaderboardClient.ProfondeurDuClassement, null, plusBas);
    }

    /// <summary>Sa ligne : le pseudo ou la poignee du joueur du lancement (jamais « local », qui n'a pas de compte).</summary>
    internal static bool EstMoi(string joueur, string poignee, string moi)
        => moi.Length > 0 && moi != "local"
           && (string.Equals(joueur.Trim(), moi.Trim(), StringComparison.OrdinalIgnoreCase)
               || (poignee.Length > 0 && string.Equals(poignee.Trim().TrimStart('@'), moi.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase)));

    // ─────────────────────────────── evenements de la partie ───────────────────────────────

    private void Recevoir(EventEnvelope e)
    {
        try
        {
            switch (e.Type)
            {
                case "ui.game.started":
                    Demarrer();
                    break;
                case "ui.game.ended":
                    Terminer();
                    break;
                case "score.live.changed":
                    Score(e.Payload);
                    break;
                case "speedrun.started":
                case "speedrun.attempt.opened":
                    Course();
                    break;
                case "speedrun.attempt.ended":
                    Arrivee(e.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _journal?.LogDebug(ex, "PlayerDeck : evenement {Type} ignore pour le rang.", e.Type);
        }
    }

    /// <summary>Le jeu demarre : le meme jeu que le lancement arme (moins de deux minutes), sinon le rang s'eteint.</summary>
    private void Demarrer()
    {
        var chemin = _contexte.Ui.Running?.GamePath ?? "";
        Armement? arme;
        lock (_verrou)
        {
            arme = _arme;
            if (arme is null) return;
            if (arme.Demarre) return;   // ES annonce parfois deux fois le meme depart
            if (DateTime.UtcNow - arme.Prepare > DureeDeLArmement || !MemeChemin(arme.Lancement.GamePath, chemin))
            {
                arme.Arret.Cancel();
                _arme = null;
                _publie = null;
                return;
            }

            arme.Demarre = true;
        }

        Calculer(arme);
    }

    private void Terminer()
    {
        Armement? arme;
        lock (_verrou)
        {
            arme = _arme;
            if (arme is null || !arme.Demarre) return;
            arme.Arret.Cancel();
            arme.Fini = true;
        }

        Calculer(arme);
        lock (_verrou)
        {
            if (ReferenceEquals(_arme, arme)) _arme = null;
        }
    }

    private void Score(object? payload)
    {
        var joueur = (int)(DeckStateService.Entier(DeckStateService.Champ(payload, "Player")) ?? 1);
        if (DeckStateService.Entier(DeckStateService.Champ(payload, "Score")) is not { } score) return;
        Armement? arme;
        lock (_verrou)
        {
            arme = _arme;
            if (arme?.Classement is not { } c || c.Board != DeckRankBoard.Jeu || joueur != c.Place || arme.Valeur == score) return;
            arme.Valeur = score;
        }

        Calculer(arme);
    }

    /// <summary>Une tentative d'epreuve part (ou repart) : le chrono monte, le rang n'est plus fixe.</summary>
    private void Course()
    {
        Armement? arme;
        lock (_verrou)
        {
            arme = _arme;
            if (arme?.Classement is not { Board: DeckRankBoard.Epreuve }) return;
            arme.Arrivee = null;
        }

        Calculer(arme);
    }

    /// <summary>
    /// Une tentative finie : son temps officiel fixe le rang, s'il est classable (arrivee, temps du jeu sans ecart). Un
    /// classement d'epreuve sur une autre mesure que le temps ne se fixe pas ici (seul le temps est publie a l'arrivee).
    /// </summary>
    private void Arrivee(object? payload)
    {
        var p = payload is JsonElement j ? j : JsonSerializer.SerializeToElement(payload);
        if (p.ValueKind != JsonValueKind.Object) return;
        var etat = p.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        long? frames = p.TryGetProperty("official", out var o) && o.ValueKind == JsonValueKind.Object
                       && o.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var n) ? n : null;
        var ecart = p.TryGetProperty("demo", out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("gameTimeCheck", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == "ecart";
        Armement? arme;
        lock (_verrou)
        {
            arme = _arme;
            if (arme?.Classement is not { Board: DeckRankBoard.Epreuve } classement) return;
            arme.Arrivee = etat == SessionSpeedrun.Finished && frames is { } t && !ecart && classement.Key == DeckRankBoard.CleTemps ? t : null;
        }

        Calculer(arme);
    }

    // ─────────────────────────────── calcul et publication ───────────────────────────────

    /// <summary>Recalcule le rang de cet armement et le publie s'il a change. Local, sans reseau.</summary>
    private void Calculer(Armement arme)
    {
        DeckRank rang;
        lock (_verrou)
        {
            if (!ReferenceEquals(_arme, arme)) return;
            rang = DeckRankCalcul.Composer(arme.Lancement, arme.Classement, arme.Table, arme.Monde, arme.Valeur, arme.Arrivee, arme.Fini,
                _etat?.LaunchIdCourant);
            if (_publie is not null && _publie with { Revision = 0 } == rang) return;
            rang = rang with { Revision = ++_revision };
            _publie = rang;
        }

        _ = _bus.PublishAsync(new EventEnvelope { Type = "deck.rank", Payload = rang });
    }

    private static bool MemeChemin(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b.Trim('"')), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Armement(DeckLancement lancement, DeckRankBoard? classement, DateTime prepare, string monde)
    {
        public DeckLancement Lancement { get; } = lancement;
        public DeckRankBoard? Classement { get; } = classement;
        public DateTime Prepare { get; } = prepare;
        public string Monde { get; } = monde;
        public DeckRankTable? Table;
        public long? Valeur;
        public long? Arrivee;
        public bool Demarre;
        public bool Fini;
        public readonly CancellationTokenSource Arret = new();
    }
}

/// <summary>
/// Le classement contre lequel un lancement avec Deck se classe. <c>Board</c> : scores (le classement d'un jeu, une regle :
/// 1cc, 1lc, 1cc-multi, un mode...) | challenges (le classement d'une epreuve de Challenges &amp; Speedruns). <c>Key</c> :
/// score | time. <c>Place</c> : le joueur dont le score se classe (1 ; la place du joueur local en MULTIPLAYER).
/// <c>FrameRate</c> : la cadence d'un temps compte en images (affichage m:ss.cc). <c>Label</c> : le code du mode (« 1CC »).
/// </summary>
public sealed record DeckRankBoard(
    string Board,
    string RomGroup,
    string Ruleset,
    string Key,
    bool LowerIsBetter,
    int Place = 1,
    double? FrameRate = null,
    string? Label = null)
{
    public const string Jeu = "scores";
    public const string Epreuve = "challenges";
    public const string CleScore = "score";
    public const string CleTemps = "time";

    /// <summary>Le classement d'un jeu pour une regle ; le sens viendra des lignes lues (« better »).</summary>
    public static DeckRankBoard DuJeu(string romGroup, string regle, IReadOnlyList<string>? regles = null, int place = 1)
        => new(Jeu, romGroup, regle, CleScore, false, place, null, regle.Length > 0 ? LeaderboardInputService.LibelleDeRegle(regle, regles) : null);

    /// <summary>Le classement d'une epreuve : sa premiere mesure dit la cle (images = temps) et le sens.</summary>
    public static DeckRankBoard DeLEpreuve(Reglement reglement)
    {
        var critere = reglement.Rang.FirstOrDefault() ?? new CritereDeRang("frames", true);
        var temps = critere.Mesure.Equals("frames", StringComparison.OrdinalIgnoreCase);
        return new(Epreuve, reglement.RomGroup, reglement.Ruleset, temps ? CleTemps : CleScore, critere.PlusPetitMeilleur, 1,
            temps ? reglement.Cadence : null, reglement.EstUnChallenge ? "CHALLENGE" : "SPEEDRUN");
    }
}

/// <summary>Une ligne du classement lu : rang du site, joueur, valeur (nombre), monde (home, station, stream ; vide : sans monde), la sienne.</summary>
public sealed record DeckRankRow(int Rank, string Player, long Value, string World, bool IsMe);

/// <summary>
/// Le classement lu au lancement. <c>Source</c> : fresh (lu a l'instant, ou cache frais du client) | stale (le dernier
/// connu : le site ne repond pas) | offline (rien de connu). <c>Truncated</c> : la liste ne va pas jusqu'au bout.
/// </summary>
public sealed record DeckRankTable(
    IReadOnlyList<DeckRankRow> Rows,
    string Source,
    DateTime? ReadAtUtc,
    bool Truncated,
    int? Total,
    bool? LowerIsBetter)
{
    public const string Frais = "fresh";
    public const string Perime = "stale";
    public const string HorsLigneSource = "offline";

    public static readonly DeckRankTable HorsLigne = new([], HorsLigneSource, null, false, null, null);
}

/// <summary>Une ligne citee par le rang (le rang au-dessus, la cible, le record). <c>Text</c> : un temps ecrit m:ss.cc.</summary>
public sealed record DeckRankEntry(int Rank, string Player, long Value, string? World, bool IsMe, string? Text);

/// <summary>
/// deck.rank : la place que vaudrait la partie en cours (H-D21). Toujours PROVISOIRE (<c>Provisional</c> = true) : seul le
/// score certifie entre au classement.
/// <c>State</c> : loading (classement en lecture) | ranked (rang calcule) | first_score (classement vide : la partie serait
/// le premier score) | racing (classement au temps, course en cours : <c>Target</c> et <c>PersonalBest</c>, pas de rang)
/// | offline (rien de connu) | no_board (aucun classement pour ce jeu et ce mode) | ended (partie finie).
/// <c>Source</c> : fresh | stale | offline | none. <c>Rank</c> = 1 + lignes strictement meilleures ; <c>RankAtLeast</c> :
/// au-dela de la liste lue ; <c>Of</c> : lignes du classement, la partie comprise (<c>OfAtLeast</c> : liste tronquee) ;
/// <c>Tied</c> : un autre a la meme valeur. <c>Above</c> : la ligne juste au-dessus (a egaler pour prendre son rang),
/// <c>Gap</c> : l'ecart avec elle, toujours positif. <c>Target</c> : en course, le temps du rang vise (juste au-dessus du
/// record perso, sinon la 10e place). <c>NewPersonalBest</c> : la valeur classee bat le record perso lu. <c>Leader</c> : le
/// premier du classement lu (record du monde d'une epreuve, meilleur score d'un jeu), pour la ligne WR du Deck.
/// </summary>
public sealed record DeckRank(
    string? LaunchId,
    string Mode,
    string Game,
    string Player,
    string Board,
    string? Ruleset,
    string? Label,
    string Key,
    string Better,
    bool Provisional,
    string State,
    string Source,
    DateTime? BoardReadAtUtc,
    int Rows,
    string World,
    long? Value,
    string? ValueText,
    int? Rank,
    bool RankAtLeast,
    int? Of,
    bool OfAtLeast,
    bool Tied,
    DeckRankEntry? Above,
    long? Gap,
    string? GapText,
    DeckRankEntry? Target,
    DeckRankEntry? PersonalBest,
    bool NewPersonalBest,
    bool Final,
    double? FrameRate,
    long Revision,
    DeckRankEntry? Leader = null);

/// <summary>Le calcul du rang, pur : il se teste sans reseau ni bus.</summary>
public static class DeckRankCalcul
{
    /// <summary>La place a prendre pour entrer dans le top, quand le joueur n'a pas de record (comme le cartouche du defi).</summary>
    public const int TailleDuTop = 10;

    public static bool Mieux(long a, long b, bool plusBasEstMieux) => plusBasEstMieux ? a < b : a > b;

    /// <summary>Le meme monde ; une ligne sans monde (classement d'epreuve) vaut pour tous.</summary>
    private static bool MemeMonde(string a, string b) => a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public sealed record Place(int Rang, bool RangAuMoins, int Effectif, bool EffectifAuMoins, bool Egalite, DeckRankRow? AuDessus, long? Ecart);

    /// <summary>
    /// La place de <paramref name="valeur"/> : 1 + lignes strictement meilleures (la sienne comprise, comme le site depuis le
    /// 31/10 : sous son record, sa ligne est au-dessus ; au-dessus, elle suit la partie). L'effectif compte la partie : une
    /// ligne de plus si le joueur n'en a pas dans ce monde.
    /// </summary>
    public static Place Classer(IReadOnlyList<DeckRankRow> lignes, long valeur, bool plusBasEstMieux, string monde, bool tronque, int? total)
    {
        var mieux = 0;
        var egalite = false;
        var aSaLigne = false;
        DeckRankRow? dessus = null;
        foreach (var l in lignes)
        {
            var sienne = l.IsMe && MemeMonde(l.World, monde);
            aSaLigne |= sienne;
            if (Mieux(l.Value, valeur, plusBasEstMieux))
            {
                mieux++;
                // la plus proche par la valeur ; a valeur egale, la plus basse du site (posee plus tard)
                if (dessus is null || Mieux(dessus.Value, l.Value, plusBasEstMieux) || (dessus.Value == l.Value && l.Rank > dessus.Rank)) dessus = l;
            }
            else if (l.Value == valeur && !sienne)
            {
                egalite = true;
            }
        }

        var lues = lignes.Count;
        var effectif = (total ?? lues) + (aSaLigne ? 0 : 1);
        var rang = mieux + 1;
        return new Place(
            rang,
            RangAuMoins: tronque && lues > 0 && mieux == lues,
            Effectif: Math.Max(effectif, rang),
            EffectifAuMoins: tronque && total is null,
            Egalite: egalite,
            AuDessus: dessus,
            Ecart: dessus is null ? null : Math.Abs(dessus.Value - valeur));
    }

    /// <summary>Le record perso : sa meilleure ligne dans ce monde, sinon sa meilleure ligne tout monde confondu.</summary>
    public static DeckRankRow? Record(IReadOnlyList<DeckRankRow> lignes, string monde, bool plusBasEstMieux)
    {
        DeckRankRow? ici = null, ailleurs = null;
        foreach (var l in lignes)
        {
            if (!l.IsMe) continue;
            if (MemeMonde(l.World, monde))
            {
                if (ici is null || Mieux(l.Value, ici.Value, plusBasEstMieux)) ici = l;
            }
            else if (ailleurs is null || Mieux(l.Value, ailleurs.Value, plusBasEstMieux)) ailleurs = l;
        }

        return ici ?? ailleurs;
    }

    /// <summary>
    /// Le premier du classement lu (record du monde de l'epreuve, ou meilleur score du jeu) : la meilleure valeur, a valeur
    /// egale la plus haute place du site. Null : classement vide.
    /// </summary>
    public static DeckRankRow? Premier(IReadOnlyList<DeckRankRow> lignes, bool plusBasEstMieux)
    {
        DeckRankRow? premier = null;
        foreach (var l in lignes)
            if (premier is null || Mieux(l.Value, premier.Value, plusBasEstMieux) || (l.Value == premier.Value && l.Rank < premier.Rank)) premier = l;
        return premier;
    }

    /// <summary>
    /// Le rang vise pendant une course (classement au temps) : la ligne juste au-dessus du record perso (une autre que la
    /// sienne) ; deja premier, son record ; sans record, la 10e place (ou la derniere). Null : classement vide.
    /// </summary>
    public static DeckRankRow? Cible(IReadOnlyList<DeckRankRow> lignes, string monde, bool plusBasEstMieux)
    {
        var record = Record(lignes, monde, plusBasEstMieux);
        if (record is not null)
        {
            DeckRankRow? cible = null;
            foreach (var l in lignes)
            {
                if (l.IsMe || !Mieux(l.Value, record.Value, plusBasEstMieux)) continue;
                if (cible is null || Mieux(cible.Value, l.Value, plusBasEstMieux) || (cible.Value == l.Value && l.Rank > cible.Rank)) cible = l;
            }

            return cible ?? record;
        }

        var autres = lignes.Where(l => !l.IsMe).OrderBy(l => l.Rank).ToList();
        return autres.Count == 0 ? null : autres[Math.Min(TailleDuTop, autres.Count) - 1];
    }

    /// <summary>
    /// Le message deck.rank d'un instant : etat, rang, ligne au-dessus, ecart, cible, record. <paramref name="valeur"/> : le
    /// dernier score du joueur (classement d'un jeu) ; <paramref name="arrivee"/> : le temps officiel d'une epreuve finie.
    /// </summary>
    public static DeckRank Composer(DeckLancement lancement, DeckRankBoard? classement, DeckRankTable? table, string monde, long? valeur,
        long? arrivee, bool fini, string? launchId)
    {
        var plusBas = table?.LowerIsBetter ?? classement?.LowerIsBetter ?? false;
        var cle = classement?.Board == DeckRankBoard.Epreuve
            ? classement.Key
            : plusBas ? DeckRankBoard.CleTemps : classement?.Key ?? DeckRankBoard.CleScore;
        var cadence = classement?.FrameRate;
        var vide = new DeckRank(launchId, lancement.Mode, lancement.Game, lancement.Player, classement?.Board ?? "none", classement?.Ruleset,
            classement?.Label, cle, plusBas ? "lower" : "higher", true, "no_board", "none", table?.ReadAtUtc, table?.Rows.Count ?? 0, monde,
            null, null, null, false, null, false, false, null, null, null, null, null, false, false, cadence, 0);

        if (classement is null) return fini ? vide with { State = "ended" } : vide;
        if (table is null) return vide with { State = fini ? "ended" : "loading", Source = "none" };

        var lignes = table.Rows;
        var record = Record(lignes, monde, plusBas);
        var etat = vide with { Source = table.Source, PersonalBest = Entree(record, cadence), Leader = Entree(Premier(lignes, plusBas), cadence) };
        if (fini) return etat with { State = "ended" };
        if (lignes.Count == 0)
        {
            // Rien de connu : on ne dit pas « premier » sur un classement qu'on n'a pas pu lire
            if (table.Source == DeckRankTable.HorsLigneSource) return etat with { State = "offline" };
            var premier = cle == DeckRankBoard.CleTemps && arrivee is null ? null : (int?)1;
            return etat with
            {
                State = "first_score", Rank = premier, Of = premier, Value = arrivee ?? (cle == DeckRankBoard.CleTemps ? null : valeur),
                ValueText = Temps(arrivee, cadence), Final = arrivee is not null,
            };
        }

        // Classement au temps : pas de rang tant que la course n'est pas arrivee
        long? classee = cle == DeckRankBoard.CleTemps ? arrivee : valeur ?? 0;
        if (classee is not { } v)
        {
            return etat with { State = "racing", Target = Entree(Cible(lignes, monde, plusBas), cadence) };
        }

        var place = Classer(lignes, v, plusBas, monde, table.Truncated, table.Total);
        // Le rang vise (ecran de depart du Deck, 09/11) : comme en course, la ligne juste au-dessus du record perso, sinon la 10e
        // sans record lu, toute valeur classee en est un (un score nul n'en est pas un)
        var nouveau = record is null ? cle == DeckRankBoard.CleTemps || v > 0 : Mieux(v, record.Value, plusBas);
        return etat with
        {
            State = "ranked",
            Value = v,
            ValueText = Temps(cle == DeckRankBoard.CleTemps ? v : null, cadence),
            Rank = place.Rang,
            RankAtLeast = place.RangAuMoins,
            Of = place.Effectif,
            OfAtLeast = place.EffectifAuMoins,
            Tied = place.Egalite,
            Above = Entree(place.AuDessus, cadence),
            Gap = place.Ecart,
            GapText = cle == DeckRankBoard.CleTemps ? Temps(place.Ecart, cadence) : null,
            NewPersonalBest = nouveau,
            Final = arrivee is not null,
            Target = Entree(Cible(lignes, monde, plusBas), cadence),
        };
    }

    private static DeckRankEntry? Entree(DeckRankRow? l, double? cadence)
        => l is null ? null : new DeckRankEntry(l.Rank, l.Player, l.Value, l.World.Length > 0 ? l.World : null, l.IsMe, Temps(l.Value, cadence));

    private static string? Temps(long? images, double? cadence)
        => images is { } n && cadence is > 0 ? Chronometrage.Afficher(n, cadence.Value) : null;
}
