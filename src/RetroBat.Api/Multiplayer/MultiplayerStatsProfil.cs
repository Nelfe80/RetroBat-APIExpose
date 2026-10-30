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

    /// <summary>
    /// Le calme apres un changement de manche, en millisecondes : les regles qui lisent l'adversaire se taisent le temps que
    /// la vie se recharge (KOF '98). 0 : aucun.
    /// </summary>
    [JsonPropertyName("settle_after_round_ms")]
    public int SettleAfterRoundMs { get; init; }

    [JsonPropertyName("signals")]
    public IReadOnlyDictionary<string, MultiplayerStatsSignal> Signals { get; init; } = new Dictionary<string, MultiplayerStatsSignal>();

    [JsonPropertyName("counters")]
    public IReadOnlyDictionary<string, MultiplayerStatsCompteur> Counters { get; init; } = new Dictionary<string, MultiplayerStatsCompteur>();

    [JsonPropertyName("metrics")]
    public IReadOnlyList<MultiplayerStatsMesure> Metrics { get; init; } = [];

    /// <summary>
    /// LA PRESENCE D'UNE PLACE (CDC Classements v1.1, lot C n° 30 ; Bomber Man World : l'ordinateur reprend la place d'un
    /// humain elimine, et ses bombes monteraient les statistiques de la place) : tant que la place n'est pas en jeu, ses
    /// valeurs sont suivies sans rien compter. Absent : toujours en jeu.
    /// </summary>
    [JsonPropertyName("presence")]
    public MultiplayerStatsPresence? Presence { get; init; }

    /// <summary>Les trois categories du CDC, dans l'ordre de l'ecran.</summary>
    public static readonly IReadOnlyList<string> Categories = ["execution", "efficacite", "maitrise"];

    /// <summary>Les regles de compteur connues : la liste fermee du CDC.</summary>
    public static readonly IReadOnlySet<string> Regles = new HashSet<string>(StringComparer.Ordinal)
    {
        RegleBaisseAdverse, RegleSommeBaisseAdverse, RegleAtteintAdverse, RegleMancheGagneeSiPlein, ReglePlusGrandeBaisseAdverse,
        RegleHausse, RegleSommeHausse, RegleHausseDAuMoins, RegleAtteint, ReglePlusGrandeHausse, RegleMaximum,
    };

    /// <summary>Une baisse de la valeur adverse (une vie) : un coup reussi, credite a l'autre place.</summary>
    public const string RegleBaisseAdverse = "baisse_adverse";

    /// <summary>La somme des baisses de la valeur adverse : les degats infliges (un K.O. compte la vie qui restait).</summary>
    public const string RegleSommeBaisseAdverse = "somme_baisse_adverse";

    /// <summary>
    /// La plus grande baisse d'un coup de la valeur adverse : le meilleur coup (28/10, Yu Yu Hakusho). Des coups enchaines
    /// plus vite que deux lectures (80 ms) n'en font qu'un : c'est alors le meilleur enchainement.
    /// </summary>
    public const string ReglePlusGrandeBaisseAdverse = "plus_grande_baisse_adverse";

    /// <summary>La valeur adverse atteint une valeur (le K.O.) : compte une fois a chaque passage.</summary>
    public const string RegleAtteintAdverse = "atteint_adverse";

    /// <summary>Le compteur de manches de la place monte d'un, sa valeur pleine : un perfect.</summary>
    public const string RegleMancheGagneeSiPlein = "manche_gagnee_si_plein";

    /// <summary>
    /// Une hausse de la valeur de la place (un compteur du jeu : pieces posees, lignes) : comptee une fois. Une baisse (le
    /// compteur remis a zero a la manche suivante) ne compte rien, et la hausse d'apres repart de la.
    /// </summary>
    public const string RegleHausse = "hausse";

    /// <summary>La somme des hausses de la valeur de la place : les lignes effacees, sur toutes les manches.</summary>
    public const string RegleSommeHausse = "somme_hausse";

    /// <summary>Une hausse d'au moins <c>value</c> d'un coup (un TETRIS : quatre lignes a la fois), comptee une fois.</summary>
    public const string RegleHausseDAuMoins = "hausse_d_au_moins";

    /// <summary>La valeur de la place atteint <c>value</c> : comptee une fois a chaque passage (la premiere piece d'une manche).</summary>
    public const string RegleAtteint = "atteint";

    /// <summary>La plus grande hausse d'un coup de la valeur de la place : le meilleur coup (la plus grosse chaine d'un puzzle).</summary>
    public const string ReglePlusGrandeHausse = "plus_grande_hausse";

    /// <summary>La plus haute valeur atteinte par la place (le record de pieces sur une course, la plus longue chaine).</summary>
    public const string RegleMaximum = "maximum";

    /// <summary>Les regles qui lisent l'adversaire : elles demandent un duel (deux places).</summary>
    public bool LitLAdversaire => Counters.Values.Any(c => LitLAdversaireSelon(c.Rule));

    public static bool LitLAdversaireSelon(string regle)
        => regle is RegleBaisseAdverse or RegleSommeBaisseAdverse or RegleAtteintAdverse or ReglePlusGrandeBaisseAdverse;

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
            if (signal.Source != "poll" || signal.Bytes < 1 || signal.Bytes > (signal.Digits ? 8 : 4) || signal.Order is not ("le" or "be" or "68k")
                || (signal.Digits && signal.Bcd) || signal.Memory is not (null or MultiplayerChoice.CarteDuCoeur))
            {
                return "signal « " + nom + " » : source, taille, ordre ou memoire inconnu";
            }
            if (signal.Pointer is { } pointeur && (pointeur.Decalage is null || pointeur.Origine is null))
            {
                return "signal « " + nom + " » : pointeur illisible";
            }
            if (signal.Min is { } min && signal.Max is { } max && min > max)
            {
                return "signal « " + nom + " » : bornes inversees";
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
                case RegleAtteintAdverse or RegleAtteint when compteur.Cible(signal) is null:
                    return "compteur « " + nom + " » : valeur a atteindre inconnue";
                case RegleMancheGagneeSiPlein when signal.Full is null || compteur.Rounds is null || !profil.Signals.ContainsKey(compteur.Rounds):
                    return "compteur « " + nom + " » : valeur pleine ou manches manquantes";
                case RegleHausseDAuMoins when compteur.Cible(signal) is not > 0:
                    return "compteur « " + nom + " » : hausse minimale inconnue";
            }
        }

        if (profil.Presence is { } presence && (!profil.Signals.ContainsKey(presence.Signal)
                || (presence.Mask is { } masques && Enumerable.Range(1, profil.Players).Any(place => !masques.TryGetValue(place.ToString(CultureInfo.InvariantCulture), out var m) || m <= 0))))
        {
            return "presence : signal inconnu ou masque manquant";
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
                || mesure.Termes() is not { Count: > 0 } termes || !termes.All(t => profil.Counters.ContainsKey(t.Compteur))
                || mesure.Better is not ("higher" or "lower"))
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

    /// <summary>
    /// <c>core_map</c> : le signal (et son pointeur) se lit dans la carte du coeur, par READ_CORE_MEMORY (CDC Classements v1.1
    /// §2) : sous mGBA Splitscreen, READ_CORE_RAM ne rend que des octets fixes, jamais la memoire du jeu.
    /// </summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    [JsonIgnore]
    public bool ParLaCarteDuCoeur => string.Equals(Memory, MultiplayerChoice.CarteDuCoeur, StringComparison.Ordinal);

    /// <summary>L'adresse de chaque place (decalage de RetroArch, premier octet lu), par numero de place.</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyDictionary<string, string> Seats { get; init; } = new Dictionary<string, string>();

    /// <summary>1 a 4 octets, lus dans l'ordre de la memoire du coeur (petit-boutiste).</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    /// <summary>
    /// L'ordre des octets : « le » (petit-boutiste, le defaut), « be » (gros-boutiste), « 68k » (un nombre du 68000 tel que
    /// FBNeo le range, octets permutes deux a deux : Neo Geo, CPS).
    /// </summary>
    [JsonPropertyName("order")]
    public string Order { get; init; } = "le";

    /// <summary>Un nombre en decimal code binaire (les scores des jeux d'arcade) : chaque quartet est un chiffre.</summary>
    [JsonPropertyName("bcd")]
    public bool Bcd { get; init; }

    /// <summary>Un chiffre par octet, son quartet bas (Rampage, CDC Classements v1.1 §7, n° 21) : jusqu'a 8 octets.</summary>
    [JsonPropertyName("digits")]
    public bool Digits { get; init; }

    /// <summary>Les octets lus, dans l'ordre de la memoire, devenus la valeur du signal ; null s'ils ne sont pas un nombre BCD.</summary>
    public int? Valeur(IReadOnlyList<int> octets)
    {
        var ordonnes = Order switch
        {
            "be" => octets.ToList(),
            "68k" => Enumerable.Range(0, octets.Count).Select(i => (i ^ 1) < octets.Count ? octets[i ^ 1] : octets[i]).ToList(),
            _ => octets.Reverse().ToList(),
        };
        long valeur = 0;
        foreach (var o in ordonnes)
        {
            if (Digits)
            {
                if ((o & 15) > 9)
                {
                    return null;
                }

                valeur = valeur * 10 + (o & 15);
            }
            else if (Bcd)
            {
                if (o >> 4 > 9 || (o & 15) > 9)
                {
                    return null;
                }

                valeur = valeur * 100 + (o >> 4) * 10 + (o & 15);
            }
            else
            {
                valeur = (valeur << 8) | (uint)o;
            }
        }

        return valeur > int.MaxValue ? null : (int)valeur;
    }

    [JsonPropertyName("full")]
    public int? Full { get; init; }

    [JsonPropertyName("ko")]
    public int? Ko { get; init; }

    /// <summary>Ne retenir qu'une valeur lue deux fois de suite : entre deux manches, un jeu remet la vie a zero octet par octet.</summary>
    [JsonPropertyName("confirm")]
    public bool Confirm { get; init; } = true;

    /// <summary>
    /// LES FICHES QUI CHANGENT DE PLACE (29/10, Bomberman PSP) : le jeu recree ses fiches de joueurs a chaque manche, ailleurs
    /// dans le tas. Avec un pointeur, l'adresse de chaque place est un ecart depuis l'endroit qu'il designe, relu a chaque
    /// passe. Sans lui, l'adresse est fixe.
    /// </summary>
    [JsonPropertyName("pointer")]
    public MultiplayerStatsPointeur? Pointer { get; init; }

    /// <summary>
    /// Les bornes de vraisemblance : une lecture hors de [min, max] ne compte pas (une fiche liberee entre deux manches se lit
    /// 0xFF, qui ferait une hausse).
    /// </summary>
    [JsonPropertyName("min")]
    public int? Min { get; init; }

    [JsonPropertyName("max")]
    public int? Max { get; init; }

    public long? Decalage(int place)
        => Seats.TryGetValue(place.ToString(CultureInfo.InvariantCulture), out var adresse)
            ? new MultiplayerChoiceSeat { CoreRam = adresse }.Decalage
            : null;

    /// <summary>La valeur si elle est vraisemblable, sinon null.</summary>
    public int? Vraisemblable(int? valeur)
        => valeur is { } v && (Min is not { } min || v >= min) && (Max is not { } max || v <= max) ? valeur : null;
}

/// <summary>
/// Un pointeur de 32 bits, petit-boutiste, a l'adresse <see cref="CoreRam"/> (decalage de RetroArch) ; le decalage qu'il
/// designe est sa valeur moins <see cref="Base"/> (0x08000000 sur PSP, ou la memoire du jeu commence a cette adresse).
/// </summary>
public sealed record MultiplayerStatsPointeur
{
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("base")]
    public string Base { get; init; } = "0x0";

    [JsonIgnore]
    public long? Decalage => new MultiplayerChoiceSeat { CoreRam = CoreRam }.Decalage;

    [JsonIgnore]
    public long? Origine => new MultiplayerChoiceSeat { CoreRam = Base }.Decalage;

    /// <summary>Le decalage designe par une valeur lue, ou null pour un pointeur nul ou en dessous de la base.</summary>
    public long? Cible(long valeur) => Origine is { } origine && valeur > origine ? valeur - origine : null;
}

/// <summary>Ce qui dit une place en jeu : un signal du profil, et le bit de chaque place ou la valeur attendue.</summary>
public sealed record MultiplayerStatsPresence
{
    [JsonPropertyName("signal")]
    public string Signal { get; init; } = string.Empty;

    /// <summary>Le bit de chaque place dans la valeur (Bomber Man World, <c>0x11316</c> : 1, 2, 4, 8) ; absent : la valeur elle-meme.</summary>
    [JsonPropertyName("mask")]
    public IReadOnlyDictionary<string, int>? Mask { get; init; }

    /// <summary>Sans masque, la valeur qui dit la place en jeu ; absente : toute valeur non nulle.</summary>
    [JsonPropertyName("value")]
    public int? Value { get; init; }

    /// <summary>La place est-elle en jeu, selon la valeur lue de son signal ? Null : on ne sait pas (rien de lu).</summary>
    public bool? EnJeu(int place, int? valeur)
        => valeur is not { } v ? null
            : Mask is { } masques ? masques.TryGetValue(place.ToString(CultureInfo.InvariantCulture), out var masque) ? (v & masque) != 0 : null
            : Value is { } attendue ? v == attendue : v != 0;
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

    /// <summary>
    /// <see cref="MultiplayerStatsProfil.RegleAtteintAdverse"/> : « ko », « full », ou un nombre ;
    /// <see cref="MultiplayerStatsProfil.RegleHausseDAuMoins"/> : la hausse minimale, un nombre.
    /// </summary>
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

    /// <summary>
    /// Le compteur montre, ou des compteurs ajoutes et retranches (« niveaux - apparitions + manches » : les lignes de TGM2,
    /// que le jeu ne garde nulle part) ; les taux et les « par minute » viendront avec le temps actif.
    /// </summary>
    [JsonPropertyName("formula")]
    public string Formula { get; init; } = string.Empty;

    /// <summary>Les compteurs de la formule et leur signe, ou null si elle est mal formee.</summary>
    public IReadOnlyList<(string Compteur, int Signe)>? Termes()
    {
        // Un compteur, puis des paires « signe compteur » : rien d'autre.
        var termes = new List<(string, int)>();
        int? signe = 1;
        foreach (var morceau in Formula.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var estUnSigne = morceau is "+" or "-";
            if (estUnSigne == signe is not null)
            {
                return null;   // deux compteurs sans signe entre eux, ou deux signes de suite
            }
            if (estUnSigne)
            {
                signe = morceau == "+" ? 1 : -1;
                continue;
            }
            termes.Add((morceau, signe!.Value));
            signe = null;
        }
        return signe is null ? termes : null;
    }

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
            var termes = mesure.Termes() ?? [];
            var compteurs = termes.Select(t => Profil.Counters.GetValueOrDefault(t.Compteur)).ToList();
            var lisible = termes.Count > 0 && PlacesLues.Contains(place)
                && compteurs.All(c => c is not null && (!MultiplayerStatsProfil.LitLAdversaireSelon(c.Rule) || adversaireLu));
            if (!lisible || !Compteurs.TryGetValue(place, out var valeurs) || !termes.All(t => valeurs.ContainsKey(t.Compteur)))
            {
                return new MultiplayerStatsValeur(mesure, null, MultiplayerStatsValeur.Indisponible);
            }
            // Une difference ne descend pas sous zero : entre deux evenements d'un meme fait (la piece posee, le niveau
            // pas encore monte), elle peut valoir -1 un instant.
            var valeur = Math.Max(0, termes.Sum(t => t.Signe * valeurs[t.Compteur]));
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
