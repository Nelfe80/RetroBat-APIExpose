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

    /// <summary>
    /// LES VERSIONS LOCALISEES (decision du user du 26/11 : « en Adventure on exploite toutes les versions localisees possibles »).
    /// Une aventure est liee a UNE version (son contenu), tous ses joueurs l'ont a l'identique. Vide : le jeu n'a qu'une version.
    /// </summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<AdventureVersionDuJeu> Versions { get; init; } = [];

    [JsonPropertyName("default_version")]
    public string? DefaultVersion { get; init; }

    /// <summary>La version qui porte ce contenu (md5), ou null : un jeu a version unique, ou un contenu que le catalogue ne nomme pas.</summary>
    public AdventureVersionDuJeu? VersionDuContenu(string? md5)
        => string.IsNullOrEmpty(md5) ? null : Versions.FirstOrDefault(v => v.Content.Md5.Contains(md5, StringComparer.OrdinalIgnoreCase));

    /// <summary>La part d'Adventure qui vaut pour CE contenu : la version peut surcharger la sauvegarde de la cartouche (<c>adventure_override</c>).</summary>
    public AdventureSpec PourLeContenu(string? md5)
        => VersionDuContenu(md5)?.Override is { } surcharge
            ? this with { Cartridge = surcharge.Cartridge ?? Cartridge, FreshHint = surcharge.FreshHint ?? FreshHint }
            : this;
}

/// <summary>Une version localisee d'un jeu : son contenu exact, ses langues, et son nom pour le panneau. Ses departs pretes sont ceux du jeu (<c>content</c> de chaque depart).</summary>
public sealed record AdventureVersionDuJeu
{
    /// <summary>Stable, minuscules : sert d'identifiant dans l'interface et de dossier des etats.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("languages")]
    public IReadOnlyList<string> Languages { get; init; } = [];

    [JsonPropertyName("content")]
    public MultiplayerContent Content { get; init; } = new();

    /// <summary>Le nom du fichier de la borne : pour l'affichage seulement, jamais pour l'identite.</summary>
    [JsonPropertyName("file_hint")]
    public string? FileHint { get; init; }

    [JsonPropertyName("adventure_override")]
    public AdventureSpec? Override { get; init; }
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
    public string? File { get; init; } = "{rom}.srm";

    /// <summary>
    /// Ce qu'est la sauvegarde : <c>gba_srm_slots</c> (le .srm de mGBA Splitscreen : 4 emplacements de 32 768 octets, un par console),
    /// <c>snes_sram</c>, ou <c>password</c> (Lost Vikings : le jeu garde un mot de passe, aucun fichier : <c>file</c> est alors null).
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>Un jeu sans fichier de sauvegarde (mot de passe) : rien a poser, rien a relire, la reprise se fait par l'etat.</summary>
    [JsonIgnore]
    public bool SansFichier => string.IsNullOrEmpty(File) || string.Equals(Kind, "password", StringComparison.OrdinalIgnoreCase);
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
    /// d'Adventure du jeu) ; les versions localisees sont fondues (<see cref="FusionnerLesVersions"/>). Les departs prets sont ceux
    /// de MULTIPLAYER (<c>state</c>, <c>sha256</c>, <c>players</c>, <c>content</c>, chemin relatif au dossier du catalogue).
    /// </summary>
    internal static JsonNode? NormaliserLeNoeud(JsonNode? racine)
    {
        if (racine?["games"] is not JsonArray jeux)
        {
            return racine;
        }

        foreach (var jeu in jeux.OfType<JsonObject>())
        {
            FusionnerLesVersions(jeu);

            // Un « core » au niveau du jeu REMPLACE le coeur du systeme, options comprises (MultiplayerCatalogue.CoreOf) : le site lit
            // ce nom, mais ici le jeu du meme coeur que son systeme reprend les options du systeme (splitscreen_players={players}...).
            if (jeu["core"] is JsonValue nom && nom.TryGetValue<string>(out var coeurDuJeu)
                && jeu["system"]?.GetValue<string>() is { } systeme
                && racine["cores"]?[systeme]?["core"]?.GetValue<string>() is { } coeurDuSysteme
                && string.Equals(coeurDuJeu, coeurDuSysteme, StringComparison.Ordinal))
            {
                jeu.Remove("core");
            }
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

        }

        return racine;
    }

    /// <summary>
    /// PLUSIEURS VERSIONS D'UN JEU (CONTRAT.md, proposition du lot 0) : pour le lecteur de MULTIPLAYER, un jeu a plusieurs contenus
    /// est deja un jeu dont <c>content.md5</c> en liste plusieurs et dont chaque depart porte le <c>content</c> qu'il vaut. Les
    /// versions sont donc fondues : les md5, crc et tailles de toutes les versions s'ajoutent a <c>content</c> (la version par
    /// defaut d'abord : c'est l'ordre de preference), et leurs departs a <c>starts</c> ; <c>start</c> prend le premier depart de la
    /// version par defaut. La cle <c>versions</c> reste pour <see cref="LesSpecs(Stream)"/>.
    /// </summary>
    internal static void FusionnerLesVersions(JsonObject jeu)
    {
        if (jeu["versions"] is not JsonArray versions || versions.Count == 0)
        {
            return;
        }

        var parDefaut = jeu["default_version"]?.GetValue<string>();
        var ordre = versions.OfType<JsonObject>().OrderBy(v => string.Equals(v["key"]?.GetValue<string>(), parDefaut, StringComparison.Ordinal) ? 0 : 1).ToList();
        var contenu = jeu["content"] as JsonObject ?? new JsonObject();
        var md5 = new List<string>();
        var crc = new List<string>();
        var taille = new List<long>();
        var departs = new List<JsonNode>();
        foreach (var version in ordre)
        {
            var c = version["content"] as JsonObject;
            md5.AddRange((c?["md5"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? []);
            crc.AddRange((c?["crc"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? []);
            taille.AddRange((c?["size"] as JsonArray)?.Select(n => n!.GetValue<long>()) ?? []);
            if (version["start"] is JsonObject depart)
            {
                departs.Add(depart.DeepClone());
            }

            foreach (var autre in (version["starts"] as JsonArray) ?? [])
            {
                if (autre is not null)
                {
                    departs.Add(autre.DeepClone());
                }
            }
        }

        // Les md5 du jeu lui-meme (ancien lecteur) restent, apres ceux des versions, sans doublon.
        md5.AddRange((contenu["md5"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? []);
        crc.AddRange((contenu["crc"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? []);
        taille.AddRange((contenu["size"] as JsonArray)?.Select(n => n!.GetValue<long>()) ?? []);
        jeu["content"] = new JsonObject
        {
            ["md5"] = new JsonArray(md5.Distinct(StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["crc"] = new JsonArray(crc.Distinct(StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["size"] = new JsonArray(taille.Distinct().Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
        };
        if (departs.Count > 0)
        {
            jeu["start"] = departs[0].DeepClone();
            jeu["starts"] = new JsonArray(departs.Skip(1).ToArray());
        }
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
            var spec = entree["adventure"]?.Deserialize<AdventureSpec>(Lecture);
            // Les versions et la version par defaut sont a cote de la cle « adventure », au niveau du jeu.
            var versions = entree["versions"]?.Deserialize<List<AdventureVersionDuJeu>>(Lecture);
            if (!string.IsNullOrWhiteSpace(id) && (spec is not null || versions is { Count: > 0 }))
            {
                specs[id] = (spec ?? new AdventureSpec()) with { Versions = versions ?? [], DefaultVersion = entree["default_version"]?.GetValue<string>() };
            }
        }

        return specs;
    }
}
