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

    /// <summary>Le coeur d'un jeu : le sien s'il en nomme un, sinon celui de son systeme.</summary>
    public MultiplayerCore? CoreOf(MultiplayerGame jeu)
    {
        if (jeu.Core is { Length: > 0 } propre)
        {
            return new MultiplayerCore { Core = propre };
        }

        return Cores.TryGetValue(jeu.System, out var coeur) ? coeur : null;
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

    /// <summary>Un coeur propre a ce jeu, a la place de celui de son systeme.</summary>
    [JsonPropertyName("core")]
    public string? Core { get; init; }

    [JsonIgnore]
    public bool EstArcade => string.Equals(System, "arcade", StringComparison.OrdinalIgnoreCase);
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

public static class MultiplayerLevels
{
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
