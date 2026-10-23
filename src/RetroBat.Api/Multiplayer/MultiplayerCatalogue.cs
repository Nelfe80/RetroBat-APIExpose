using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// Le catalogue NELFEPLAY MULTIPLAYER, tel que le Data Pack le livre
/// (<c>multiplayer/catalogue.json</c>, installe dans <c>resources/multiplayer/</c>).
///
/// C'est lui qui fait foi, pas le nombre de joueurs du referentiel : un jeu a plusieurs joueurs
/// n'est pas forcement jouable en netplay (TMNT et Gauntlet II divergeaient a la sonde du 21/10),
/// et le compte du referentiel est parfois faux (OutRunners y est note a un joueur).
///
/// L'identite suit la regle des jeux (11/10) : en arcade le NOM DU SET, que FBNeo controle au
/// chargement ; en console le CONTENU mesure (md5). Le CRC et la taille ne decident rien : ils
/// evitent seulement de hacher tout un dossier de ROMs.
/// </summary>
public sealed record MultiplayerCatalogue
{
    public const int SchemaVersionSupportee = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("revision")]
    public string? Revision { get; init; }

    /// <summary>Le coeur impose par systeme, et ses options (identiques sur toutes les bornes, ou propres a la place).</summary>
    [JsonPropertyName("cores")]
    public IReadOnlyDictionary<string, MultiplayerCore> Cores { get; init; } = new Dictionary<string, MultiplayerCore>();

    [JsonPropertyName("games")]
    public IReadOnlyList<MultiplayerGame> Games { get; init; } = [];

    /// <summary>
    /// Le coeur d'un jeu : le sien s'il en nomme un, sinon celui de son systeme ; ses options
    /// propres s'ajoutent a celles du systeme et l'emportent sur elles.
    /// </summary>
    public MultiplayerCore? CoreOf(MultiplayerGame jeu)
    {
        MultiplayerCore? coeur = jeu.Core is { Length: > 0 } propre
            ? new MultiplayerCore { Core = propre }
            : Cores.TryGetValue(jeu.System, out var duSysteme) ? duSysteme : null;
        if (coeur == null || jeu.Options is not { Count: > 0 } options)
        {
            return coeur;
        }

        var fusion = new Dictionary<string, string>(coeur.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (cle, valeur) in options)
        {
            fusion[cle] = valeur;
        }

        return coeur with { Options = fusion };
    }
}

public sealed record MultiplayerCore
{
    [JsonPropertyName("core")]
    public string Core { get; init; } = string.Empty;

    /// <summary>Les memes sur toutes les bornes : le netplay l'exige. <c>{players}</c> = joueurs de la gameroom.</summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Propres a chaque borne (ce qu'elle affiche, ce qu'elle fait entendre). <c>{seat}</c> = sa place.</summary>
    [JsonPropertyName("seat_options")]
    public IReadOnlyDictionary<string, string>? SeatOptions { get; init; }
}

public sealed record MultiplayerGame
{
    /// <summary>Cle stable du jeu dans MULTIPLAYER (<c>arcade/mslug3</c>, <c>gba/doom-ii</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Systeme canonique : <c>arcade</c> pour tous les dossiers d'arcade, sinon le dossier roms.</summary>
    [JsonPropertyName("system")]
    public string System { get; init; } = string.Empty;

    /// <summary>Arcade : le set, tel que FBNeo le nomme.</summary>
    [JsonPropertyName("set")]
    public string? Set { get; init; }

    /// <summary>
    /// Console : les contenus acceptes, le prefere d'abord. Une gameroom ne reunit que des joueurs
    /// au meme contenu.
    /// </summary>
    [JsonPropertyName("content")]
    public MultiplayerContent? Content { get; init; }

    /// <summary>Le nom du jeu, jamais traduit.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; init; } = string.Empty;

    /// <summary>Joueurs en meme temps dans la partie.</summary>
    [JsonPropertyName("players")]
    public int Players { get; init; }

    /// <summary>Joueurs d'un relais (KOF '98 en 3 contre 3), quand le jeu en propose un.</summary>
    [JsonPropertyName("relay_players")]
    public int? RelayPlayers { get; init; }

    /// <summary><c>classique</c> (netplay seul, menus du jeu, sans classement) ou <c>nelfeplay</c>.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = MultiplayerLevels.Classique;

    /// <summary>
    /// Le type versus du jeu (combat, puzzle, sport, course, arene, fps, strategie) : sa cote par type. Null pour la coop,
    /// classee par le score en 1CC MULTI (decisions du 23/10).
    /// </summary>
    [JsonPropertyName("elo_type")]
    public string? EloType { get; init; }

    /// <summary>Un coeur propre a ce jeu, a la place de celui de son systeme.</summary>
    [JsonPropertyName("core")]
    public string? Core { get; init; }

    /// <summary>Options du coeur propres a ce jeu (un reglage DIP d'arcade), les memes sur toutes les bornes.</summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>
    /// Les ecrans que le coeur dessine cote a cote : une borne d'origine a un moniteur par joueur
    /// (World Rally 2, OutRunners). Chaque borne n'en affiche qu'un, celui de sa place.
    /// </summary>
    [JsonPropertyName("screens")]
    public int Screens { get; init; } = 1;

    /// <summary>L'ecran de chaque place, 1 etant le plus a gauche (Hard Dunk : trois joueurs par ecran).</summary>
    [JsonPropertyName("seat_screens")]
    public IReadOnlyList<int>? SeatScreens { get; init; }

    /// <summary>
    /// NIVEAU NELFEPLAY : le depart pret (V24-15). Un etat que l'hote charge avant que les autres se connectent ;
    /// ils le recoivent avec la connexion (verifie le 22/10 sur Street Fighter II' CE, deux RetroArch).
    /// </summary>
    [JsonPropertyName("start")]
    public MultiplayerStart? Start { get; init; }

    /// <summary>NIVEAU NELFEPLAY : ce que chaque joueur choisit (le personnage par ▲▼), ecrit chez l'hote apres l'etat.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<MultiplayerChoice>? Choices { get; init; }

    /// <summary>
    /// NIVEAU NELFEPLAY : la fin d'une rencontre (V24-16), lue chez l'hote ; la plateforme ferme alors le jeu, sinon le
    /// gagnant d'un SF2 continuerait seul contre l'ordinateur.
    /// </summary>
    [JsonPropertyName("end")]
    public MultiplayerEnd? End { get; init; }

    /// <summary>
    /// LES AUTRES TYPES DE FIN (decision du user du 23/10) : <c>score_final</c> d'abord (les sports). Une cle a part : une
    /// API d'avant, qui ne connait que <see cref="End"/>, l'ignore et garde le jeu en Classique.
    /// </summary>
    [JsonPropertyName("match_end")]
    public MultiplayerMatchEnd? MatchEnd { get; init; }

    /// <summary>La fin de rencontre se lit-elle (manches, ou un type de <see cref="MatchEnd"/> connu) ?</summary>
    [JsonIgnore]
    public bool FinLue => End is not null || MatchEnd is { EstConnu: true };

    /// <summary>Le delai avant de fermer le jeu, la rencontre finie, le temps de voir le resultat.</summary>
    [JsonIgnore]
    public int DelaiDeFermeture => End?.CloseAfterS ?? MatchEnd?.CloseAfterS ?? 5;

    /// <summary>NIVEAU NELFEPLAY : ce que l'hote mesure pendant un duel pour l'ecran de resultat (00quater.3, ecran 10).</summary>
    [JsonPropertyName("stats")]
    public MultiplayerStats? Stats { get; init; }

    [JsonIgnore]
    public bool EstArcade => string.Equals(System, "arcade", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool EstNelfePlay => Level == MultiplayerLevels.NelfePlay && Start is not null;

    /// <summary>
    /// Classe (23/10) : un jeu versus dont on lit la fin, quel que soit son niveau. Le depart pret du niveau NelfePlay
    /// reste un plus, pas une condition de la cote.
    /// </summary>
    [JsonIgnore]
    public bool EstClasse => FinLue && EloType is { Length: > 0 } type && MultiplayerLevels.TypesVersus.Contains(type);

    /// <summary>L'ecran a afficher pour cette place (1 a <see cref="Screens"/>).</summary>
    public int EcranDeLaPlace(int place)
    {
        if (Screens <= 1)
        {
            return 1;
        }

        if (SeatScreens is { Count: > 0 } ecrans && place >= 1 && place <= ecrans.Count)
        {
            return ecrans[place - 1];
        }

        // Sans table : les places se repartissent dans l'ordre, a parts egales.
        var parEcran = Math.Max(1, (int)Math.Ceiling(Math.Max(Players, 1) / (double)Screens));
        return Math.Clamp((Math.Max(place, 1) - 1) / parEcran + 1, 1, Screens);
    }
}

public sealed record MultiplayerContent
{
    [JsonPropertyName("md5")]
    public IReadOnlyList<string> Md5 { get; init; } = [];

    /// <summary>Prefiltre : le CRC d'une entree de zip se lit sans decompresser.</summary>
    [JsonPropertyName("crc")]
    public IReadOnlyList<string> Crc { get; init; } = [];

    /// <summary>Prefiltre d'un fichier nu.</summary>
    [JsonPropertyName("size")]
    public IReadOnlyList<long> Size { get; init; } = [];
}

/// <summary>L'etat d'un depart pret, livre par le Data Pack sous <c>resources/multiplayer/</c>.</summary>
public sealed record MultiplayerStart
{
    /// <summary>Chemin relatif au dossier du catalogue : <c>states/arcade/sf2ce/selection-2p-fbneo.state</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>L'empreinte de l'etat : un fichier retouche ne se charge pas.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>
/// Un choix du joueur parmi des valeurs permises (S5 du socle commun : jamais une valeur libre), et l'endroit ou
/// l'ecrire pour chaque place. L'adresse est celle que RetroArch lit et ecrit (<c>READ_CORE_RAM</c>) : le decalage
/// dans la memoire systeme du coeur, deja inverse sur les cartes 68000.
/// </summary>
public sealed record MultiplayerChoice
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("values")]
    public IReadOnlyList<MultiplayerChoiceValue> Values { get; init; } = [];

    [JsonPropertyName("default")]
    public int Default { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    /// <summary>La valeur suivante (ou precedente) dans la liste, en boucle.</summary>
    public int Voisine(int valeur, int pas)
    {
        if (Values.Count == 0)
        {
            return valeur;
        }

        var index = Values.ToList().FindIndex(v => v.Value == valeur);
        index = index < 0 ? 0 : ((index + pas) % Values.Count + Values.Count) % Values.Count;
        return Values[index].Value;
    }

    public string NomDe(int valeur) => Values.FirstOrDefault(v => v.Value == valeur)?.Name ?? string.Empty;
}

/// <summary>
/// La fin d'une rencontre : la place dont le compteur atteint <see cref="FirstTo"/>, seule en tete, gagne (les manches de
/// SF2CE, mesurees le 22/10 sous FBNeo : P1 0xFF864E, P2 0xFF894E, 2 pour gagner), et seulement pendant le duel quand
/// une garde le dit (<see cref="Duel"/>). Le jeu se ferme <see cref="CloseAfterS"/> secondes plus tard, le temps de voir
/// le vainqueur.
/// </summary>
public sealed record MultiplayerEnd
{
    [JsonPropertyName("first_to")]
    public int FirstTo { get; init; }

    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    [JsonPropertyName("close_after_s")]
    public int CloseAfterS { get; init; } = 5;

    /// <summary>
    /// LA GARDE DE DUEL (vague 1 du 23/10) : l'octet qui dit que les deux joueurs sont dans le match, et sa valeur. Sans
    /// elle, une manche gagnee par l'ordinateur contre le joueur 1, avant l'arrivee du joueur 2, comptait dans la case du
    /// joueur 2 (mesure au banc : Windjammers, Magical Drop III, Super Puzzle Fighter II Turbo). Absente : les compteurs
    /// comptent toujours, comme sur SF2CE, ou le depart pret met les deux joueurs en duel.
    /// </summary>
    [JsonPropertyName("duel")]
    public MultiplayerDuelGuard? Duel { get; init; }
}

/// <summary>
/// UNE FIN AU SCORE (<c>score_final</c>, decision du user du 23/10) : quand l'octet <see cref="Over"/> prend sa valeur (fin
/// du match, chronometre a zero), la place au score le plus haut gagne ; a egalite, match nul. Un signal de fin deja la au
/// depart (le match d'avant) ne compte pas : il faut l'avoir vu retomber. Garde de duel facultative, comme <see cref="MultiplayerEnd.Duel"/>.
/// </summary>
public sealed record MultiplayerMatchEnd
{
    public const string ScoreFinal = "score_final";

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("close_after_s")]
    public int CloseAfterS { get; init; } = 5;

    /// <summary>Le signal de fin de match : l'octet et la valeur qu'il prend quand le match est fini.</summary>
    [JsonPropertyName("over")]
    public MultiplayerDuelGuard? Over { get; init; }

    /// <summary>Le score de chaque place (decalage de RetroArch, premier octet lu).</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    [JsonPropertyName("score")]
    public MultiplayerScoreFormat Score { get; init; } = new();

    [JsonPropertyName("duel")]
    public MultiplayerDuelGuard? Duel { get; init; }

    [JsonIgnore]
    public bool EstConnu => Type == ScoreFinal;
}

/// <summary>Comment se lit un score : son nombre d'octets, BCD ou binaire, et l'ordre des octets lus (<c>le</c> ou <c>be</c>).</summary>
public sealed record MultiplayerScoreFormat
{
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 1;

    [JsonPropertyName("bcd")]
    public bool Bcd { get; init; }

    [JsonPropertyName("order")]
    public string Order { get; init; } = "le";

    /// <summary>La valeur des octets lus dans l'ordre de la memoire, ou null si un chiffre BCD est impossible.</summary>
    public long? Valeur(IReadOnlyList<int> octets)
    {
        var ordonnes = Order == "be" ? octets : octets.Reverse().ToList();
        long valeur = 0;
        foreach (var o in ordonnes)
        {
            if (Bcd)
            {
                if ((o >> 4) > 9 || (o & 0x0F) > 9)
                {
                    return null;
                }

                valeur = valeur * 100 + (o >> 4) * 10 + (o & 0x0F);
            }
            else
            {
                valeur = (valeur << 8) | (uint)o;
            }
        }

        return valeur;
    }
}

/// <summary>L'octet de la garde de duel (decalage de RetroArch) et la valeur qu'il a pendant un duel.</summary>
public sealed record MultiplayerDuelGuard
{
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonIgnore]
    public long? Decalage => CoreRam.Trim() is { Length: > 2 } texte && texte.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(texte[2..], global::System.Globalization.NumberStyles.HexNumber, null, out var valeur) && valeur >= 0
            ? valeur
            : null;
}

/// <summary>
/// Les statistiques d'un duel. SF2CE (00quater.3) : un coup reussi est une baisse de la vie adverse, un K.O. une vie a
/// 0xFFFF, un perfect une manche gagnee avec la vie pleine (144).
/// </summary>
public sealed record MultiplayerStats
{
    [JsonPropertyName("life")]
    public MultiplayerStatLife? Life { get; init; }
}

public sealed record MultiplayerStatLife
{
    /// <summary>L'adresse de la vie de chaque place (decalage de RetroArch, premier octet lu).</summary>
    [JsonPropertyName("seats")]
    public IReadOnlyList<MultiplayerChoiceSeat> Seats { get; init; } = [];

    /// <summary>1 ou 2 octets, lus dans l'ordre de la memoire du coeur (petit-boutiste).</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; } = 2;

    [JsonPropertyName("full")]
    public int Full { get; init; }

    /// <summary>La valeur du K.O. (SF2CE : 0xFFFF).</summary>
    [JsonPropertyName("ko")]
    public int Ko { get; init; } = 0xFFFF;
}

public sealed record MultiplayerChoiceValue
{
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary>Le nom a l'ecran (un nom de personnage, jamais traduit).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Le portrait carre du personnage : un badge public de RetroAchievements (SF2CE : les succes « finir le jeu avec »),
    /// que chaque borne telecharge dans son propre cache. Aucune image du jeu ne passe par le Data Pack.
    /// </summary>
    [JsonPropertyName("ra_badge")]
    public string? RaBadge { get; init; }
}

public sealed record MultiplayerChoiceSeat
{
    [JsonPropertyName("seat")]
    public int Seat { get; init; }

    /// <summary>Le decalage en hexadecimal (<c>0x864E</c>).</summary>
    [JsonPropertyName("core_ram")]
    public string CoreRam { get; init; } = string.Empty;

    [JsonIgnore]
    public long? Decalage => CoreRam.Trim() is { Length: > 2 } texte && texte.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(texte[2..], global::System.Globalization.NumberStyles.HexNumber, null, out var valeur) && valeur >= 0
            ? valeur
            : null;
}

/// <summary>Les resultats d'une rencontre, tels que la plateforme les recoit.</summary>
public static class MultiplayerResultats
{
    /// <summary>
    /// LE MATCH NUL (decision du user du 23/10) : la place gagnante 0. Un score egal au coup de sifflet, une arrivee ex
    /// aequo : 0,5 chacun pour la cote, une rencontre menee a son terme, jamais un abandon.
    /// </summary>
    public const int MatchNul = 0;
}

public static class MultiplayerLevels
{
    /// <summary>Les types versus de la v1 (champ <c>elo_type</c>) : la cote par type du joueur.</summary>
    public static readonly IReadOnlySet<string> TypesVersus = new HashSet<string>(StringComparer.Ordinal)
        { "combat", "puzzle", "sport", "course", "arene", "fps", "strategie" };

    public const string Classique = "classique";
    public const string NelfePlay = "nelfeplay";
}

/// <summary>Lit le catalogue livre par le Data Pack, et le refuse s'il n'est pas sain.</summary>
public sealed class MultiplayerCatalogueStore
{
    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<MultiplayerCatalogueStore>? _logger;
    private readonly object _verrou = new();
    private (DateTime Ecrit, long Taille, MultiplayerCatalogue? Catalogue, string? Erreur) _cache;

    public MultiplayerCatalogueStore(ILogger<MultiplayerCatalogueStore>? logger = null, string? path = null)
    {
        _logger = logger;
        Path = path ?? System.IO.Path.Combine(RetroBatPaths.PluginRoot, "resources", "multiplayer", "catalogue.json");
    }

    public string Path { get; }

    /// <summary>
    /// Le catalogue courant, relu seulement quand le fichier change. Null avec une raison quand
    /// il manque ou ne se lit pas : la collection garde alors ce qu'elle avait.
    /// </summary>
    public MultiplayerCatalogue? Lire(out string? erreur)
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                erreur = "catalogue absent (Data Pack pas encore synchronise)";
                return null;
            }

            lock (_verrou)
            {
                if (_cache.Ecrit == info.LastWriteTimeUtc && _cache.Taille == info.Length)
                {
                    erreur = _cache.Erreur;
                    return _cache.Catalogue;
                }
            }

            MultiplayerCatalogue? catalogue;
            using (var flux = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                catalogue = JsonSerializer.Deserialize<MultiplayerCatalogue>(flux, Lecture);
            }

            erreur = Valider(catalogue);
            var retenu = erreur == null ? Nettoyer(catalogue!) : null;
            if (erreur != null)
            {
                _logger?.LogWarning("Catalogue MULTIPLAYER refuse : {Erreur}", erreur);
            }

            lock (_verrou)
            {
                _cache = (info.LastWriteTimeUtc, info.Length, retenu, erreur);
            }

            return retenu;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            erreur = "catalogue illisible : " + ex.GetType().Name;
            return null;
        }
    }

    internal static string? Valider(MultiplayerCatalogue? catalogue)
    {
        if (catalogue == null)
        {
            return "catalogue vide";
        }

        if (catalogue.SchemaVersion != MultiplayerCatalogue.SchemaVersionSupportee)
        {
            return "schema " + catalogue.SchemaVersion + " inconnu (attendu " + MultiplayerCatalogue.SchemaVersionSupportee + ")";
        }

        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jeu in catalogue.Games)
        {
            if (string.IsNullOrWhiteSpace(jeu.Id) || !vus.Add(jeu.Id.Trim()))
            {
                return "identifiant vide ou en double : « " + jeu.Id + " »";
            }

            if (string.IsNullOrWhiteSpace(jeu.System))
            {
                return jeu.Id + " : systeme manquant";
            }

            if (jeu.EstArcade ? string.IsNullOrWhiteSpace(jeu.Set) : (jeu.Content?.Md5.Count ?? 0) == 0)
            {
                // Sans identite, le jeu entrerait sur la foi d'un nom de fichier.
                return jeu.Id + (jeu.EstArcade ? " : set manquant" : " : aucun md5 de contenu");
            }

            if (jeu.Players < 2)
            {
                return jeu.Id + " : moins de deux joueurs";
            }

            if (jeu.Level is not (MultiplayerLevels.Classique or MultiplayerLevels.NelfePlay))
            {
                return jeu.Id + " : niveau « " + jeu.Level + " » inconnu";
            }

            if (jeu.Screens is < 1 or > 4
                || (jeu.SeatScreens is { Count: > 0 } ecrans
                    && (ecrans.Count < jeu.Players || ecrans.Any(ecran => ecran < 1 || ecran > jeu.Screens))))
            {
                // Une place sans ecran, ou un ecran qui n'existe pas : une borne afficherait la vue d'un autre.
                return jeu.Id + " : ecrans par place incoherents";
            }

            if (ValiderLeDepart(jeu) is { } depart)
            {
                return jeu.Id + " : " + depart;
            }
        }

        return null;
    }

    /// <summary>
    /// Le niveau NelfePlay exige son depart pret (V24-15) ; chaque choix a des valeurs permises distinctes, un defaut
    /// parmi elles, et une adresse pour chaque place. Un choix sans adresse pour une place laisserait ce joueur au
    /// personnage de l'etat sans le lui dire.
    /// </summary>
    internal static string? ValiderLeDepart(MultiplayerGame jeu)
    {
        if (jeu.Level == MultiplayerLevels.NelfePlay)
        {
            if (jeu.Start is null || string.IsNullOrWhiteSpace(jeu.Start.State))
            {
                return "niveau NelfePlay sans depart pret";
            }

            if (jeu.Start.State.Contains("..", StringComparison.Ordinal) || global::System.IO.Path.IsPathRooted(jeu.Start.State))
            {
                return "chemin de l'etat hors du dossier MULTIPLAYER";
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(jeu.Start.Sha256.Trim(), "^[0-9a-fA-F]{64}$"))
            {
                return "empreinte de l'etat manquante";
            }
        }

        if (jeu.End is { } fin)
        {
            if (fin.FirstTo is < 1 or > 255 || fin.CloseAfterS is < 0 or > 60)
            {
                return "fin de rencontre incoherente";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (fin.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "fin de rencontre : pas d'adresse pour la place " + place;
                }
            }

            if (fin.Duel is { } garde && (garde.Decalage is null || garde.Value is < 0 or > 255))
            {
                return "fin de rencontre : garde de duel incoherente";
            }
        }

        if (jeu.MatchEnd is { } autre)
        {
            if (!autre.EstConnu)
            {
                return "fin de rencontre : type « " + autre.Type + " » inconnu";
            }

            if (autre.Over?.Decalage is null || autre.Over.Value is < 0 or > 255 || autre.CloseAfterS is < 0 or > 60)
            {
                return "fin de rencontre : signal de fin incoherent";
            }

            if (autre.Score.Bytes is < 1 or > 4 || autre.Score.Order is not ("le" or "be"))
            {
                return "fin de rencontre : format du score incoherent";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (autre.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "fin de rencontre : pas de score pour la place " + place;
                }
            }

            if (autre.Duel is { } garde && (garde.Decalage is null || garde.Value is < 0 or > 255))
            {
                return "fin de rencontre : garde de duel incoherente";
            }
        }

        if (jeu.Stats?.Life is { } vie)
        {
            if (vie.Bytes is < 1 or > 2 || vie.Full < 1)
            {
                return "statistiques incoherentes";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (vie.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "statistiques : pas d'adresse de vie pour la place " + place;
                }
            }
        }

        foreach (var choix in jeu.Choices ?? [])
        {
            if (string.IsNullOrWhiteSpace(choix.Key) || choix.Values.Count == 0
                || choix.Values.Select(v => v.Value).Distinct().Count() != choix.Values.Count
                || choix.Values.Any(v => v.Value is < 0 or > 255 || string.IsNullOrWhiteSpace(v.Name)))
            {
                return "choix « " + choix.Key + " » sans valeurs permises distinctes";
            }

            if (choix.Values.All(v => v.Value != choix.Default))
            {
                return "choix « " + choix.Key + " » : defaut hors des valeurs";
            }

            for (var place = 1; place <= jeu.Players; place++)
            {
                if (choix.Seats.FirstOrDefault(s => s.Seat == place)?.Decalage is null)
                {
                    return "choix « " + choix.Key + " » : pas d'adresse pour la place " + place;
                }
            }
        }

        return null;
    }

    /// <summary>Les empreintes en minuscules, sans espace : la comparaison n'a plus a y penser.</summary>
    internal static MultiplayerCatalogue Nettoyer(MultiplayerCatalogue catalogue) => catalogue with
    {
        Games = catalogue.Games.Select(jeu => jeu with
        {
            Id = jeu.Id.Trim(),
            System = jeu.System.Trim().ToLowerInvariant(),
            Set = jeu.Set?.Trim().ToLowerInvariant(),
            Content = jeu.Content is null ? null : jeu.Content with
            {
                Md5 = jeu.Content.Md5.Select(Propre).Where(valeur => valeur.Length > 0).ToList(),
                Crc = jeu.Content.Crc.Select(Propre).Where(valeur => valeur.Length > 0).ToList(),
            },
        }).ToList(),
    };

    private static string Propre(string? valeur) => (valeur ?? string.Empty).Trim().ToLowerInvariant();
}
