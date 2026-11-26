using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Adventure;

/// <summary>
/// Ce qu'une entree du catalogue Adventure ajoute a un jeu MULTIPLAYER (cle <c>adventure</c> de l'entree). Le reste de l'entree
/// (identite, coeur, options, ports par place, <c>launcher_options</c>) est exactement celui du catalogue MULTIPLAYER : meme
/// mecanique, meme lecture (<see cref="MultiplayerCatalogueStore"/>), rien de redeveloppe.
/// </summary>
public sealed record AdventureSpec
{
    /// <summary>Le moins de joueurs d'une seance (la borne n'ouvre pas une aventure a un joueur : c'est un jeu a plusieurs).</summary>
    [JsonPropertyName("min_players")]
    public int MinPlayers { get; init; } = 2;

    /// <summary>La sauvegarde du jeu de la cartouche de l'aventure, ou la poser et la relire.</summary>
    [JsonPropertyName("cartridge_save")]
    public AdventureCartridge? Cartridge { get; init; }

    /// <summary>Un mot pour le panneau : comment la partie neuve demarre (ex. « choisir MULTI PLAYER, GAME PAK SELECTION : P1 »).</summary>
    [JsonPropertyName("fresh_hint")]
    public string? FreshHint { get; init; }
}

public sealed record AdventureCartridge
{
    /// <summary>La console dont la cartouche porte l'aventure (la place 1 : GAME PAK SELECTION de Kirby).</summary>
    [JsonPropertyName("console")]
    public int Console { get; init; } = 1;

    /// <summary>
    /// Le fichier de sauvegarde dans le dossier de sauvegardes propre a l'aventure ; <c>{rom}</c> = nom de la ROM sans
    /// extension. Defaut : <c>{rom}.srm</c> (GBA : sauvegarde de la console 1 ; SNES : la sauvegarde du jeu).
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; init; } = "{rom}.srm";
}

/// <summary>Une entree d'Adventure : le jeu (forme MULTIPLAYER) et ce qui est propre a l'aventure.</summary>
public sealed record AdventureEntree(MultiplayerGame Jeu, AdventureSpec Spec);

/// <summary>
/// LE CATALOGUE NELFEPLAY ADVENTURE, livre par le Data Pack (<c>adventure/catalogue.json</c>, installe dans
/// <c>resources/adventure/</c>). Le SITE le lit aussi (CONTRAT.md, « Catalogue Adventure ») : <c>games[]</c> avec <c>id</c>
/// (<c>gba/kirby-amazing-mirror</c>, SANS prefixe : une gameroom d'aventure se reconnait a son champ <c>adventure</c>, pas a son
/// jeu), <c>system</c>, <c>name</c>, <c>players</c> ({min, max} ; un entier est aussi admis), <c>content.md5</c>, <c>core</c>. Le
/// reste est la forme MULTIPLAYER (<c>cores</c> avec options, <c>launcher_options</c>, <c>seat_ports</c>...), lue par le meme
/// <see cref="MultiplayerCatalogueStore"/> ; la cle <c>adventure</c> par jeu porte ce qui est propre a l'aventure.
/// </summary>
public sealed class AdventureCatalogueStore
{
    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<AdventureCatalogueStore>? _logger;

    public AdventureCatalogueStore(ILogger<AdventureCatalogueStore>? logger = null, string? path = null, Version? api = null)
    {
        _logger = logger;
        Path = path ?? System.IO.Path.Combine(RetroBatPaths.PluginRoot, "resources", "adventure", "catalogue.json");
        Interne = new MultiplayerCatalogueStore(null, Path, api, flux => Normaliser(flux));
    }

    public string Path { get; }

    /// <summary>
    /// Le lecteur de MULTIPLAYER, sur le fichier d'Adventure et avec la lecture d'Adventure : la collection d'ES
    /// (<see cref="MultiplayerCollectionSyncService"/> avec son profil) le lit tel quel.
    /// </summary>
    public MultiplayerCatalogueStore Interne { get; }

    /// <summary>Le catalogue courant (jeux valides), ou null avec la raison.</summary>
    public MultiplayerCatalogue? Lire(out string? erreur) => Interne.Lire(out erreur);

    /// <summary>Les entrees avec leur part d'Adventure (une entree sans la cle <c>adventure</c> prend les valeurs par defaut).</summary>
    public IReadOnlyList<AdventureEntree> Entrees(out string? erreur)
    {
        var catalogue = Lire(out erreur);
        if (catalogue is null)
        {
            return [];
        }

        var specs = LesSpecs(Path);
        return catalogue.Games.Select(jeu => new AdventureEntree(jeu, specs.TryGetValue(jeu.Id, out var spec) ? spec : new AdventureSpec())).ToList();
    }

    /// <summary>
    /// La forme du site vers la forme MULTIPLAYER : <c>players</c> en {min, max} devient le maximum (le minimum passe a la part
    /// d'Adventure du jeu) ; <c>starts</c> du site (id, nom, empreinte d'un depart pret) n'est pas un depart de MULTIPLAYER et
    /// n'est pas lu en v1. Le reste est inchange.
    /// </summary>
    internal static JsonNode? NormaliserLeNoeud(JsonNode? racine)
    {
        if (racine?["games"] is not JsonArray jeux)
        {
            return racine;
        }

        foreach (var jeu in jeux.OfType<JsonObject>())
        {
            if (jeu["players"] is JsonObject bornes)
            {
                var min = bornes["min"]?.GetValue<int>();
                var max = bornes["max"]?.GetValue<int>() ?? min ?? 0;
                jeu["players"] = max;
                if (min is { } minimum && jeu["adventure"] is not JsonObject { } existante)
                {
                    jeu["adventure"] = new JsonObject { ["min_players"] = minimum };
                }
                else if (min is { } m2 && jeu["adventure"] is JsonObject propre && propre["min_players"] is null)
                {
                    propre["min_players"] = m2;
                }
            }

            jeu.Remove("starts");
        }

        return racine;
    }

    internal static MultiplayerCatalogue? Normaliser(Stream flux)
        => NormaliserLeNoeud(JsonNode.Parse(flux, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            ?.Deserialize<MultiplayerCatalogue>(Lecture);

    /// <summary>La cle <c>adventure</c> de chaque entree, par identifiant. Fichier illisible : rien (la collection le dit deja).</summary>
    internal static IReadOnlyDictionary<string, AdventureSpec> LesSpecs(string chemin)
    {
        try
        {
            using var flux = File.Open(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LesSpecs(flux);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, AdventureSpec>(StringComparer.Ordinal);
        }
    }

    internal static IReadOnlyDictionary<string, AdventureSpec> LesSpecs(Stream flux)
    {
        var specs = new Dictionary<string, AdventureSpec>(StringComparer.Ordinal);
        var noeud = NormaliserLeNoeud(JsonNode.Parse(flux, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));
        foreach (var entree in noeud?["games"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = entree["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id) && entree["adventure"]?.Deserialize<AdventureSpec>(Lecture) is { } spec)
            {
                specs[id] = spec;
            }
        }

        return specs;
    }
}
