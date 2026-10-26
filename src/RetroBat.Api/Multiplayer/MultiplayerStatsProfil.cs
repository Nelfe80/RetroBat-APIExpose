using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// LE PROFIL DE STATISTIQUES D'UN JEU (CDC des statistiques, lot 2 ; decisions du user du 23/10, audit du lot 1) : livre
/// par le Data Pack (<c>multiplayer/stats/&lt;systeme&gt;/&lt;set&gt;.json</c>, reference par <c>stats.profile</c> au
/// catalogue), il decrit les signaux lus en memoire, les compteurs qu'en tirent des regles d'une liste FERMEE (ecrites et
/// testees dans <see cref="MultiplayerStatsMoteur"/> : aucun code dans le profil), et les trois statistiques de l'ecran de
/// fin, une par categorie : Execution, Efficacite, Maitrise. L'hote les calcule et les envoie ; l'invite aussi, pour son
/// ecran et pour que le site compare.
/// </summary>
public sealed record MultiplayerStatsProfil
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>La version du profil : une mesure ne se compare qu'a la meme version. 0 = profil implicite (bloc <c>life</c>).</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("players")]
    public int Players { get; init; } = 2;

    [JsonPropertyName("signals")]
    public IReadOnlyDictionary<string, MultiplayerStatsSignal> Signals { get; init; } = new Dictionary<string, MultiplayerStatsSignal>();

    [JsonPropertyName("counters")]
    public IReadOnlyDictionary<string, MultiplayerStatsCompteur> Counters { get; init; } = new Dictionary<string, MultiplayerStatsCompteur>();

    [JsonPropertyName("metrics")]
    public IReadOnlyList<MultiplayerStatsMesure> Metrics { get; init; } = [];

    /// <summary>Les trois categories du CDC, dans l'ordre de l'ecran.</summary>
    public static readonly IReadOnlyList<string> Categories = ["execution", "efficacite", "maitrise"];

    /// <summary>Les regles de compteur connues : la liste fermee du CDC.</summary>
    public static readonly IReadOnlySet<string> Regles = new HashSet<string>(StringComparer.Ordinal)
    {
        RegleBaisseAdverse, RegleSommeBaisseAdverse, RegleAtteintAdverse, RegleMancheGagneeSiPlein,
    };

    /// <summary>Une baisse de la valeur adverse (une vie) : un coup reussi, credite a l'autre place.</summary>
    public const string RegleBaisseAdverse = "baisse_adverse";

    /// <summary>La somme des baisses de la valeur adverse : les degats infliges (un K.O. compte la vie qui restait).</summary>
    public const string RegleSommeBaisseAdverse = "somme_baisse_adverse";

    /// <summary>La valeur adverse atteint une valeur (le K.O.) : compte une fois a chaque passage.</summary>
    public const string RegleAtteintAdverse = "atteint_adverse";

    /// <summary>Le compteur de manches de la place monte d'un, sa valeur pleine : un perfect.</summary>
    public const string RegleMancheGagneeSiPlein = "manche_gagnee_si_plein";

    /// <summary>Les regles qui lisent l'adversaire : elles demandent un duel (deux places).</summary>
    public bool LitLAdversaire => Counters.Values.Any(c => c.Rule is RegleBaisseAdverse or RegleSommeBaisseAdverse or RegleAtteintAdverse);

    /// <summary>
    /// Le profil d'un jeu : son fichier (<c>stats.profile</c>), sinon le profil IMPLICITE de son bloc <c>stats.life</c>
    /// (le comportement d'avant les profils, version 0), sinon aucun. Un fichier illisible ou invalide retombe sur le
    /// profil implicite, avec l'erreur : il ne prive pas le jeu de ses statistiques.
    /// </summary>
    public static MultiplayerStatsProfil? Pour(MultiplayerGame jeu, out string? erreur, string? dossierDuCatalogue = null)
    {
        erreur = null;
        if (jeu.Stats?.Profile is { Length: > 0 } relatif)
        {
            var profil = Charger(relatif, dossierDuCatalogue ?? DossierDuCatalogue, out erreur);
            if (profil is not null)
            {
                return profil;
            }
        }
        return Implicite(jeu);
    }

    /// <summary>Le dossier du catalogue MULTIPLAYER installe par le Data Pack.</summary>
    public static string DossierDuCatalogue => Path.Combine(RetroBatPaths.PluginRoot, "resources", "multiplayer");

    /// <summary>Lit et valide un profil, relatif au dossier du catalogue, sous <c>stats/</c> ; null et l'erreur sinon.</summary>
    public static MultiplayerStatsProfil? Charger(string relatif, string dossierDuCatalogue, out string? erreur)
    {
        erreur = null;
        if (!relatif.StartsWith("stats/", StringComparison.Ordinal) || !relatif.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || !RetroBat.Api.Infrastructure.DataPackPaths.CheminRelatifSur(relatif))
        {
            erreur = "chemin de profil refuse : " + relatif;
            return null;
        }

        var chemin = Path.Combine(dossierDuCatalogue, relatif.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var profil = JsonSerializer.Deserialize<MultiplayerStatsProfil>(File.ReadAllText(chemin), Lecture);
            erreur = profil is null ? "profil vide" : Valider(profil);
            return erreur is null ? profil : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            erreur = "profil illisible (" + relatif + ") : " + ex.Message;
            return null;
        }
    }

    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Le profil d'avant les profils, tire du bloc <c>stats.life</c> et des manches de <c>end</c> : coups, K.O., perfects et
    /// degats, montres en Execution (coups), Efficacite (degats), Maitrise (perfects). Sans manches au catalogue, les
    /// perfects ne se mesurent pas.
    /// </summary>
    public static MultiplayerStatsProfil? Implicite(MultiplayerGame jeu)
    {
        if (jeu.Stats?.Life is not { Seats.Count: > 0 } vie)
        {
            return null;
        }

        var signaux = new Dictionary<string, MultiplayerStatsSignal>(StringComparer.Ordinal)
        {
            ["vie"] = new()
            {
                Seats = vie.Seats.ToDictionary(s => s.Seat.ToString(CultureInfo.InvariantCulture), s => s.CoreRam),
                Bytes = vie.Bytes, Full = vie.Full, Ko = vie.Ko,
            },
        };
        var compteurs = new Dictionary<string, MultiplayerStatsCompteur>(StringComparer.Ordinal)
        {
            ["coups"] = new() { Rule = RegleBaisseAdverse, Signal = "vie" },
            ["ko"] = new() { Rule = RegleAtteintAdverse, Signal = "vie", Value = "ko" },
            ["degats"] = new() { Rule = RegleSommeBaisseAdverse, Signal = "vie" },
        };
        if (jeu.End is { Seats.Count: > 0 } fin)
        {
            // Les manches se lisent telles quelles, comme le guet de la fin de rencontre.
            signaux["manches"] = new()
            {
                Seats = fin.Seats.ToDictionary(s => s.Seat.ToString(CultureInfo.InvariantCulture), s => s.CoreRam),
                Bytes = 1, Confirm = false,
            };
            compteurs["perfects"] = new() { Rule = RegleMancheGagneeSiPlein, Signal = "vie", Rounds = "manches" };
        }

        return new MultiplayerStatsProfil
        {
            Id = jeu.Id + "/implicite",
            Version = 0,
            Players = 2,
            Signals = signaux,
            Counters = compteurs,
            Metrics =
            [
                new() { Category = "execution", Id = "coups", Label = "multiplayer.stat.hits", Formula = "coups" },
                new() { Category = "efficacite", Id = "degats", Label = "multiplayer.stat.damage", Formula = "degats", Unit = "points" },
                new() { Category = "maitrise", Id = "perfects", Label = "multiplayer.stat.perfects", Formula = "perfects" },
            ],
        };
    }

    /// <summary>Ce qui ne va pas dans un profil, ou null : une erreur le refuse entier.</summary>
    public static string? Valider(MultiplayerStatsProfil profil)
    {
        if (profil.Id.Length is 0 or > 100 || profil.Version < 1 || profil.Players is < 2 or > 8)
        {
            return "identite du profil incoherente";
        }

        foreach (var (nom, signal) in profil.Signals)
        {
            if (signal.Source != "poll" || signal.Bytes is < 1 or > 4)
            {
                return "signal « " + nom + " » : source ou taille inconnue";
            }
            for (var place = 1; place <= profil.Players; place++)
            {
                if (signal.Decalage(place) is null)
                {
                    return "signal « " + nom + " » : pas d'adresse pour la place " + place;
                }
            }
        }

        foreach (var (nom, compteur) in profil.Counters)
        {
            if (!Regles.Contains(compteur.Rule))
            {
                return "compteur « " + nom + " » : regle inconnue « " + compteur.Rule + " »";
            }
            if (!profil.Signals.TryGetValue(compteur.Signal, out var signal))
            {
                return "compteur « " + nom + " » : signal inconnu";
            }
            switch (compteur.Rule)
            {
                case RegleAtteintAdverse when compteur.Cible(signal) is null:
                    return "compteur « " + nom + " » : valeur a atteindre inconnue";
                case RegleMancheGagneeSiPlein when signal.Full is null || compteur.Rounds is null || !profil.Signals.ContainsKey(compteur.Rounds):
                    return "compteur « " + nom + " » : valeur pleine ou manches manquantes";
            }
        }

        if (profil.LitLAdversaire && profil.Players != 2)
        {
            return "les regles qui lisent l'adversaire demandent un duel";
        }
        if (profil.Metrics.Count != Categories.Count
            || !Categories.All(c => profil.Metrics.Count(m => m.Category == c) == 1))
        {
            return "il faut une statistique par categorie (execution, efficacite, maitrise)";
        }
        foreach (var mesure in profil.Metrics)
        {
            if (mesure.Id.Length is 0 or > 32 || !mesure.Id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                || !profil.Counters.ContainsKey(mesure.Formula) || mesure.Better is not ("higher" or "lower"))
            {
                return "statistique « " + mesure.Id + " » incoherente";
            }
        }
        return profil.Metrics.Select(m => m.Id).Distinct().Count() == profil.Metrics.Count ? null : "statistiques en double";
    }
}

public sealed record MultiplayerStatsSignal
{
    /// <summary>« poll » : lu par l'API toutes les 80 ms (les evenements du wrapper viendront avec un pilote qui en a besoin).</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "poll";

    /// <summary>L'adresse de chaque place (decalage de RetroArch, premier octet lu), par numero de place.</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyDictionary<string, string> Seats { get; init; } = new Dictionary<string, string>();

    /// <summary>1 a 4 octets, lus dans l'ordre de la memoire du coeur (petit-boutiste).</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    [JsonPropertyName("full")]
    public int? Full { get; init; }

    [JsonPropertyName("ko")]
    public int? Ko { get; init; }

    /// <summary>Ne retenir qu'une valeur lue deux fois de suite : entre deux manches, un jeu remet la vie a zero octet par octet.</summary>
    [JsonPropertyName("confirm")]
    public bool Confirm { get; init; } = true;

    public long? Decalage(int place)
        => Seats.TryGetValue(place.ToString(CultureInfo.InvariantCulture), out var adresse)
            ? new MultiplayerChoiceSeat { CoreRam = adresse }.Decalage
            : null;
}

public sealed record MultiplayerStatsCompteur
{
    [JsonPropertyName("rule")]
    public string Rule { get; init; } = string.Empty;

    [JsonPropertyName("signal")]
    public string Signal { get; init; } = string.Empty;

    /// <summary><see cref="MultiplayerStatsProfil.RegleMancheGagneeSiPlein"/> : le signal des manches de chaque place.</summary>
    [JsonPropertyName("rounds")]
    public string? Rounds { get; init; }

    /// <summary><see cref="MultiplayerStatsProfil.RegleAtteintAdverse"/> : « ko », « full », ou un nombre.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>La valeur a atteindre, lue dans le signal pour « ko » et « full ».</summary>
    public int? Cible(MultiplayerStatsSignal signal) => Value switch
    {
        "ko" => signal.Ko,
        "full" => signal.Full,
        { } texte when int.TryParse(texte, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };
}

public sealed record MultiplayerStatsMesure
{
    /// <summary><c>execution</c>, <c>efficacite</c> ou <c>maitrise</c>.</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>La cle du texte du nom de la mesure (<c>multiplayer.stat.hits</c>).</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>Le compteur montre : la seule formule de la V1 (les taux et les « par minute » viendront avec le temps actif).</summary>
    [JsonPropertyName("formula")]
    public string Formula { get; init; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "count";

    /// <summary><c>higher</c> ou <c>lower</c> : le sens favorable.</summary>
    [JsonPropertyName("better")]
    public string Better { get; init; } = "higher";
}

/// <summary>Une statistique d'une place a la fin : sa valeur et son etat (<c>measured</c>, <c>zero</c>, <c>unavailable</c>).</summary>
public sealed record MultiplayerStatsValeur(MultiplayerStatsMesure Mesure, long? Valeur, string Etat)
{
    public const string Mesuree = "measured";
    public const string Zero = "zero";
    public const string Indisponible = "unavailable";
}

/// <summary>Ce qu'un suivi a compte : les compteurs de chaque place, et les places dont chaque signal a ete lu.</summary>
public sealed record MultiplayerStatsResultat(
    MultiplayerStatsProfil Profil,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> Compteurs,
    IReadOnlySet<int> PlacesLues)
{
    /// <summary>
    /// Les trois statistiques d'une place. Une place dont un signal n'a jamais ete lu, ou dont l'adversaire ne l'a pas ete
    /// pour une regle qui le lit, est « non mesuree » : on ne devine pas, et zero n'est pas une absence de mesure.
    /// </summary>
    public IReadOnlyList<MultiplayerStatsValeur> MesuresDe(int place)
    {
        var adversaireLu = Compteurs.Keys.Where(p => p != place).All(PlacesLues.Contains);
        return Profil.Metrics.Select(mesure =>
        {
            var compteur = Profil.Counters.GetValueOrDefault(mesure.Formula);
            var lisible = PlacesLues.Contains(place) && (compteur is null || compteur.Rule == MultiplayerStatsProfil.RegleMancheGagneeSiPlein || adversaireLu);
            if (compteur is null || !lisible || !Compteurs.TryGetValue(place, out var valeurs) || !valeurs.TryGetValue(mesure.Formula, out var valeur))
            {
                return new MultiplayerStatsValeur(mesure, null, MultiplayerStatsValeur.Indisponible);
            }
            return new MultiplayerStatsValeur(mesure, valeur, valeur == 0 ? MultiplayerStatsValeur.Zero : MultiplayerStatsValeur.Mesuree);
        }).ToList();
    }

    /// <summary>Les quatre compteurs d'avant les profils (le site et les bornes plus anciennes les lisent), quand le profil les a.</summary>
    public StatsDeLaPlace Ancien(int place)
    {
        var valeurs = Compteurs.GetValueOrDefault(place) ?? new Dictionary<string, long>();
        int N(string nom) => (int) Math.Clamp(valeurs.GetValueOrDefault(nom), 0, int.MaxValue);
        return new StatsDeLaPlace(N("coups"), N("ko"), N("perfects"), N("degats"));
    }

    /// <summary>Les quatre compteurs d'avant les profils, pour chaque place, si le profil les a (coups, K.O., degats) ; sinon null.</summary>
    public IReadOnlyDictionary<int, StatsDeLaPlace>? AncienDesPlaces()
        => new[] { "coups", "ko", "degats" }.All(Profil.Counters.ContainsKey)
            ? Compteurs.Keys.ToDictionary(p => p, Ancien)
            : null;

    /// <summary>Le format generique envoye au site : le profil, puis chaque statistique de chaque place, valeur et etat.</summary>
    public object PourLeSite() => new
    {
        profile = new { id = Profil.Id, version = Profil.Version },
        seats = Compteurs.Keys.OrderBy(p => p).ToDictionary(
            p => p.ToString(CultureInfo.InvariantCulture),
            p => MesuresDe(p).ToDictionary(v => v.Mesure.Id, v => new { value = v.Valeur, state = v.Etat })),
    };
}
