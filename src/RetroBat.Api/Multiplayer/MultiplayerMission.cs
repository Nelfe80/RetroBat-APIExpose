using System.Globalization;
using System.Text.Json.Serialization;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LA FIN D'UNE COOP (CDC MULTIPLAYER v2.6, V24-16 et V24-18 ; CDC Ecrans v1.0 §1.1 : « MISSION ACCOMPLIE ! · GAME OVER ») :
/// ce que l'hote lit pour fermer une coop, au lieu d'attendre qu'un joueur quitte. Une cle a part, que seule une coop
/// porte : une API d'avant l'ignore, et la coop se joue comme avant, jusqu'a ce qu'on la quitte.
/// <list type="bullet">
/// <item><c>stage</c> : l'etape en cours (mission, stage, round), lue tout au long de la partie : l'ecran de fin dit la
/// derniere atteinte, avec son nom si le catalogue le donne. Ses valeurs de <c>transition</c> (MS3 : 8 entre deux missions)
/// ne sont pas des etapes ;</item>
/// <item><c>cleared</c> : le signal d'une mission accomplie (ou de la fin du jeu) ;</item>
/// <item><c>over</c> : le signal du GAME OVER de toute l'equipe ; ou <c>seats</c> : la sortie de chaque place (ses vies
/// et ses credits epuises), le GAME OVER etant la sortie de tous les humains en meme temps ;</item>
/// <item><c>close_on</c> : <c>mission</c> ferme le jeu a la premiere etape franchie (une partie = une mission, V24-16) ;
/// <c>game_over</c> (par defaut) le laisse aller jusqu'au GAME OVER ou a <c>cleared</c>, la partie entiere d'un 1CC MULTI.</item>
/// </list>
/// Une valeur n'est retenue qu'apres <see cref="SettleMs"/> sans changer, sous la garde ; rien ne finit avant d'avoir vu la
/// partie en cours (la demo, ou l'ecran de GAME OVER de la partie d'avant, ne terminent rien).
/// </summary>
public sealed record MultiplayerMissionEnd
{
    public const string FermerALaMission = "mission";
    public const string FermerAuGameOver = "game_over";

    /// <summary><c>core_map</c> : toutes les adresses se lisent par READ_CORE_MEMORY (CDC Classements v1.1 §2).</summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    /// <summary>L'octet qui dit qu'une partie se joue (ni demo, ni menu) ; hors de sa valeur, rien ne se lit.</summary>
    [JsonPropertyName("guard")]
    public MultiplayerDuelGuard? Guard { get; init; }

    [JsonPropertyName("stage")]
    public MultiplayerMissionStage? Stage { get; init; }

    [JsonPropertyName("cleared")]
    public MultiplayerDuelGuard? Cleared { get; init; }

    [JsonPropertyName("over")]
    public MultiplayerDuelGuard? Over { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerMissionSeat>? Seats { get; init; }

    [JsonPropertyName("close_on")]
    public string? CloseOn { get; init; }

    [JsonPropertyName("close_after_s")]
    public int CloseAfterS { get; init; } = 5;

    /// <summary>Le temps qu'une valeur doit tenir pour etre retenue.</summary>
    [JsonPropertyName("settle_ms")]
    public int SettleMs { get; init; } = 1000;

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.Ordinal);

    [JsonIgnore]
    public bool ALaMission => string.Equals(CloseOn, FermerALaMission, StringComparison.Ordinal);

    /// <summary>La sortie de cette place, ou null.</summary>
    public MultiplayerDuelGuard? SortieDe(int place) => Seats?.FirstOrDefault(s => s.Seat == place)?.Dead;

    /// <summary>Ce qui ne va pas dans la fin de mission de ce jeu, ou null (aucune, ou saine).</summary>
    internal static string? Valider(MultiplayerGame jeu)
    {
        if (jeu.MissionEnd is not { } fin)
        {
            return null;
        }

        if (!string.Equals(jeu.Family, "coop", StringComparison.Ordinal))
        {
            return "mission : mission_end hors d'une coop";
        }

        if (jeu.End is not null || jeu.MatchEnd is not null || jeu.RankingEnd is not null)
        {
            return "mission : mission_end avec une fin de rencontre";
        }

        if (fin.Memory is not (null or MultiplayerChoice.CarteDuCoeur))
        {
            return "mission : memoire « " + fin.Memory + " » inconnue";
        }

        if (fin.CloseOn is not (null or FermerALaMission or FermerAuGameOver))
        {
            return "mission : close_on « " + fin.CloseOn + " » inconnu";
        }

        if (fin.CloseAfterS is < 0 or > 60 || fin.SettleMs is < 0 or > 10000)
        {
            return "mission : delais incoherents";
        }

        static bool Signal(MultiplayerDuelGuard? signal) => signal is null
            || (signal.Decalage is not null && signal.Bytes is >= 1 and <= 4 && signal.Order is "le" or "be" or "68k"
                && (signal.Values ?? []).Append((long)signal.Value).All(v => v >= 0 && (signal.Bytes == 4 ? v <= uint.MaxValue : v < 1L << (8 * signal.Bytes))));
        if (!Signal(fin.Guard) || !Signal(fin.Cleared) || !Signal(fin.Over))
        {
            return "mission : garde, signal de mission ou de GAME OVER incoherent";
        }

        if (fin.Stage is { } etape && (etape.Decalage is null || etape.Bytes is < 1 or > 4 || etape.Order is not ("le" or "be" or "68k")
                || (etape.Names ?? []).Select(n => n.Value).Distinct().Count() != (etape.Names ?? []).Count))
        {
            return "mission : etape incoherente (adresse, largeur, ordre ou noms en double)";
        }

        if (fin.Seats is { Count: > 0 } places)
        {
            if (places.Any(s => s.Seat < 1 || s.Seat > jeu.Players || !Signal(s.Dead) || s.Dead is null)
                || places.Select(s => s.Seat).Distinct().Count() != places.Count)
            {
                return "mission : place hors du jeu, en double, ou sans sortie lisible";
            }

            // Une place sans sortie ne tomberait jamais : le GAME OVER de l'equipe ne se lirait pas avec elle.
            for (var place = 1; place <= jeu.Players; place++)
            {
                if (fin.SortieDe(place) is null)
                {
                    return "mission : pas de sortie pour la place " + place;
                }
            }
        }

        var finit = fin.Cleared is not null || fin.Over is not null || fin.Seats is { Count: > 0 } || (fin.ALaMission && fin.Stage is not null);
        if (!finit)
        {
            return "mission : rien ne la termine (cleared, over, seats, ou stage avec close_on « mission »)";
        }

        if (fin.ALaMission && fin.Stage is null && fin.Cleared is null)
        {
            return "mission : close_on « mission » sans etape ni signal de mission";
        }

        return null;
    }
}

/// <summary>L'etape en cours : son adresse, sa largeur, ses valeurs de transition et le nom de chaque valeur.</summary>
public sealed record MultiplayerMissionStage
{
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    [JsonPropertyName("order")]
    public string Order { get; init; } = "le";

    /// <summary>Les valeurs qui ne sont pas une etape (un ecran entre deux missions, le chargement).</summary>
    [JsonPropertyName("transition")]
    public IReadOnlyList<long>? Transition { get; init; }

    /// <summary>Le nom de chaque etape (« MISSION 2 ») ; une valeur sans nom s'affiche par son rang, de base 1.</summary>
    [JsonPropertyName("names")]
    public IReadOnlyList<MultiplayerChoiceValue>? Names { get; init; }

    [JsonIgnore]
    public long? Decalage => new MultiplayerDuelGuard { CoreRam = CoreRam }.Decalage;

    /// <summary>La valeur des octets lus, comme un signal de meme largeur et de meme ordre.</summary>
    public long? Valeur(IReadOnlyList<int> octets) => new MultiplayerDuelGuard { Bytes = Bytes, Order = Order }.Valeur(octets);

    public bool EstUneTransition(long valeur) => Transition?.Contains(valeur) == true;

    /// <summary>« MISSION 2 » si le catalogue le nomme, sinon « 2 » (la valeur de base 0, plus un).</summary>
    public string NomDe(long valeur)
        => Names?.FirstOrDefault(n => n.Value == valeur)?.Name is { Length: > 0 } nom
            ? nom
            : (valeur + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>La sortie d'une place : l'octet qui prend sa valeur quand ce joueur n'a plus ni vie ni credit.</summary>
public sealed record MultiplayerMissionSeat
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    [JsonPropertyName("dead")]
    public MultiplayerDuelGuard? Dead { get; init; }
}

/// <summary>Une lecture de la memoire d'une coop : la garde, les deux signaux, l'etape et la sortie de chaque place (null : pas lu).</summary>
public sealed record MultiplayerReleveDeMission(long? Garde, long? Accomplie, long? GameOver, long? Etape, IReadOnlyDictionary<int, long?> Sorties);

/// <summary>
/// L'ISSUE D'UNE COOP : mission accomplie ou GAME OVER ; l'etape de la fin (celle accomplie, ou la derniere atteinte) et son
/// nom ; le nombre d'etapes franchies depuis le depart.
/// </summary>
public sealed record MultiplayerFinDeMission(bool Accomplie, long? Etape, string? NomDeLEtape, int Franchies)
{
    /// <summary>La raison de fin que l'hote envoie a la plateforme (son <c>end_reason</c>, texte libre a-z et _).</summary>
    public const string RaisonAccomplie = "mission_cleared";

    public const string RaisonGameOver = "game_over";

    public string Raison => Accomplie ? RaisonAccomplie : RaisonGameOver;

    /// <summary>L'issue que dit une raison de fin de la plateforme (chez une borne qui n'a rien lu elle-meme), ou null.</summary>
    public static MultiplayerFinDeMission? DeLaRaison(string? raison) => raison switch
    {
        RaisonAccomplie => new(true, null, null, 0),
        RaisonGameOver => new(false, null, null, 0),
        _ => null,
    };

    public override string ToString()
        => (Accomplie ? "mission accomplie" : "GAME OVER") + (NomDeLEtape is { Length: > 0 } nom ? " (" + nom + ")" : string.Empty)
           + (Franchies > 0 ? ", " + Franchies.ToString(CultureInfo.InvariantCulture) + " etape(s) franchie(s)" : string.Empty);
}

/// <summary>
/// LA FIN D'UNE COOP EN COURS, sans lecture : elle recoit les releves, dans l'ordre et a leur instant, et rend l'issue quand
/// la partie est finie. Le GAME OVER passe avant la mission : une equipe qui tombe a l'ecran de transition n'a rien accompli.
/// </summary>
public sealed class MultiplayerMissionEnCours
{
    private readonly MultiplayerMissionEnd _fin;
    private readonly IReadOnlyList<int> _humains;
    private readonly Dictionary<string, (long Valeur, long Depuis)> _suivis = new(StringComparer.Ordinal);
    private bool _departVu;
    private long? _etapeDeDepart;
    private long? _derniereEtape;
    private readonly List<long> _etapesVues = [];

    public MultiplayerMissionEnCours(MultiplayerMissionEnd fin, IReadOnlyCollection<int> humains)
    {
        _fin = fin;
        _humains = humains.Distinct().OrderBy(h => h).ToList();
    }

    /// <summary>La derniere etape retenue, ou null.</summary>
    public long? Etape => _derniereEtape;

    /// <summary>L'issue de la partie si elle est finie a ce releve, sinon null.</summary>
    public MultiplayerFinDeMission? Observer(MultiplayerReleveDeMission releve, long instantMs)
    {
        if (_fin.Guard is { } garde)
        {
            if (releve.Garde is not { } lue)
            {
                return null;
            }

            // Hors de la garde (la demo, un menu), rien ne se lit ; les valeurs repartent a son retour.
            if (!garde.Vaut(lue))
            {
                _suivis.Clear();
                return null;
            }
        }

        var accomplieTenue = Signal("cleared", _fin.Cleared, releve.Accomplie, instantMs);
        var gameOverTenu = Signal("over", _fin.Over, releve.GameOver, instantMs);
        var sortiesTenues = new Dictionary<int, bool?>();
        foreach (var humain in _humains)
        {
            var sortie = _fin.SortieDe(humain);
            sortiesTenues[humain] = sortie is null ? null
                : Signal("dead" + humain.ToString(CultureInfo.InvariantCulture), sortie, releve.Sorties.GetValueOrDefault(humain), instantMs);
        }

        var etape = _fin.Stage is not null ? Tenue("stage", releve.Etape, instantMs) : null;

        // Tous les humains dehors en meme temps : le GAME OVER de l'equipe (une place que personne ne lit ne compte pas).
        var tousDehors = _fin.Seats is { Count: > 0 } && sortiesTenues.Values.All(v => v == true);
        var auMoinsUnEnJeu = _fin.Seats is not { Count: > 0 } || sortiesTenues.Values.Any(v => v == false);

        // LE DEPART : la garde tenue, aucun signal de fin, au moins un humain en jeu, et l'etape lue si le jeu en a une.
        if (!_departVu)
        {
            var calme = accomplieTenue == false || _fin.Cleared is null;
            calme &= gameOverTenu == false || _fin.Over is null;
            calme &= auMoinsUnEnJeu && !tousDehors;
            if (_fin.Stage is { } s)
            {
                calme &= etape is { } e && !s.EstUneTransition(e);
            }

            // Un signal jamais lu (null) ne prouve rien : il faut l'avoir vu a une autre valeur.
            calme &= (_fin.Cleared is null || accomplieTenue is not null) && (_fin.Over is null || gameOverTenu is not null);
            if (!calme)
            {
                return null;
            }

            _departVu = true;
            _etapeDeDepart = etape;
            _derniereEtape = etape;
            if (etape is { } premiere)
            {
                _etapesVues.Add(premiere);
            }

            return null;
        }

        if (gameOverTenu == true || tousDehors)
        {
            return Issue(false, _derniereEtape);
        }

        if (accomplieTenue == true)
        {
            return Issue(true, _derniereEtape);
        }

        // Une partie = une mission (V24-16) : l'etape quitte celle du depart, vers une transition ou l'etape suivante.
        if (_fin.ALaMission && _fin.Stage is not null && etape is { } maintenant && _etapeDeDepart is { } depart && maintenant != depart)
        {
            return Issue(true, depart);
        }

        // La partie continue : l'etape atteinte (une transition n'en est pas une).
        if (etape is { } atteinte && !_fin.Stage!.EstUneTransition(atteinte))
        {
            if (!_etapesVues.Contains(atteinte))
            {
                _etapesVues.Add(atteinte);
            }

            _derniereEtape = atteinte;
        }

        return null;
    }

    /// <summary>L'issue : les etapes franchies sont celles quittees pour une autre, plus la derniere si elle est accomplie.</summary>
    private MultiplayerFinDeMission Issue(bool accomplie, long? etape)
        => new(accomplie, etape, etape is { } e && _fin.Stage is { } s ? s.NomDe(e) : null, Math.Max(0, _etapesVues.Count - 1) + (accomplie ? 1 : 0));

    /// <summary>Le signal tenu : true a sa valeur, false a une autre, null tant qu'il n'a rien tenu (ou qu'il est absent).</summary>
    private bool? Signal(string cle, MultiplayerDuelGuard? signal, long? lue, long ms)
    {
        if (signal is null)
        {
            return null;
        }

        return Tenue(cle, lue is { } v ? (signal.Vaut(v) ? 1 : 0) : null, ms) is { } tenu ? tenu == 1 : null;
    }

    private long? Tenue(string cle, long? lue, long ms)
    {
        if (lue is { } valeur && (!_suivis.TryGetValue(cle, out var suivi) || suivi.Valeur != valeur))
        {
            _suivis[cle] = (valeur, ms);
        }

        return _suivis.TryGetValue(cle, out var tenu) && ms - tenu.Depuis >= _fin.SettleMs ? tenu.Valeur : null;
    }
}
