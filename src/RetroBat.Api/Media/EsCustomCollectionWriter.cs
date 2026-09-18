using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Ecrit une collection personnalisee d'EmulationStation dont APIExpose est le proprietaire :
/// le fichier <c>custom-&lt;nom&gt;.cfg</c>, l'entree dans <c>CollectionSystemsCustom</c>, et la
/// trace de propriete qui autorise plus tard un retrait propre.
///
/// Les collections de l'utilisateur sont intouchables : on n'ecrit que le fichier de SA
/// collection, on ne retire de la liste ES que SON nom, et on ne supprime un fichier que si
/// l'etat local dit que c'est nous qui l'avons ecrit. Un dossier <c>collections</c> n'est
/// jamais nettoye globalement.
/// </summary>
public sealed class EsCustomCollectionWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IEsSettingsStore _settingsStore;
    private readonly ILogger<EsCustomCollectionWriter>? _logger;
    private readonly string _collectionsRoot;
    private readonly string _stateRoot;

    public EsCustomCollectionWriter(
        IEsSettingsStore settingsStore,
        ILogger<EsCustomCollectionWriter>? logger = null,
        string? collectionsRoot = null,
        string? stateRoot = null)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        _collectionsRoot = collectionsRoot
            ?? Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "collections");
        _stateRoot = stateRoot
            ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay");
    }

    /// <summary>Le fichier de collection, tel qu'EmulationStation le cherche.</summary>
    public string ConfigPath(string collectionName)
        => Path.Combine(_collectionsRoot, "custom-" + NormalizeName(collectionName) + ".cfg");

    /// <summary>Notre trace de propriete, hors de l'arborescence d'EmulationStation.</summary>
    public string StatePath(string collectionName)
        => Path.Combine(_stateRoot, "collection-" + NormalizeName(collectionName) + ".json");

    /// <summary>
    /// Ecrit la collection et l'inscrit dans les reglages ES. Rien n'est touche si le contenu
    /// et les reglages sont deja ceux voulus : deux synchronisations identiques ne provoquent
    /// aucune ecriture, donc aucun reload d'EmulationStation.
    /// </summary>
    public EsCustomCollectionResult Apply(string collectionName, IEnumerable<string> gamePaths)
    {
        var nom = NormalizeName(collectionName);
        if (nom.Length == 0)
        {
            return EsCustomCollectionResult.Unchanged;
        }

        var lignes = NormalizePaths(gamePaths);
        if (lignes.Count == 0)
        {
            // Une collection vide n'a pas de raison d'exister : ES afficherait une tuile morte.
            return Remove(nom);
        }

        var contenu = string.Join(Environment.NewLine, lignes) + Environment.NewLine;
        var empreinte = Sha256(contenu);
        var etat = ReadState(nom);
        var chemin = ConfigPath(nom);
        var fichierEcrit = false;

        if (!File.Exists(chemin) ||
            etat?.ContentSha256 is not { } connu ||
            !string.Equals(connu, empreinte, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Sha256(File.ReadAllText(chemin)), empreinte, StringComparison.OrdinalIgnoreCase))
        {
            WriteAtomic(chemin, contenu);
            fichierEcrit = true;
        }

        var reglagesModifies = _settingsStore.Update(document =>
            EsCustomCollectionSettings.Add(EnsureRoot(document), nom));

        WriteState(nom, new EsCustomCollectionState
        {
            Collection = nom,
            ContentSha256 = empreinte,
            GameCount = lignes.Count,
            ListedInSettings = true,
            WrittenAtUtc = DateTime.UtcNow,
        });

        if (fichierEcrit || reglagesModifies)
        {
            _logger?.LogInformation(
                "Collection ES « {Collection} » : {Jeux} jeux{Reglages}",
                nom, lignes.Count, reglagesModifies ? ", inscrite dans CollectionSystemsCustom" : string.Empty);
        }

        return new EsCustomCollectionResult(fichierEcrit, reglagesModifies, lignes.Count);
    }

    /// <summary>
    /// Retire la collection : le fichier n'est supprime que s'il est le notre, et seul notre
    /// nom sort de <c>CollectionSystemsCustom</c>.
    /// </summary>
    public EsCustomCollectionResult Remove(string collectionName)
    {
        var nom = NormalizeName(collectionName);
        if (nom.Length == 0)
        {
            return EsCustomCollectionResult.Unchanged;
        }

        var etat = ReadState(nom);
        var chemin = ConfigPath(nom);
        var fichierSupprime = false;

        if (File.Exists(chemin))
        {
            if (etat == null)
            {
                // Un fichier du meme nom qui n'est pas de nous : il reste ou il est.
                _logger?.LogWarning(
                    "Collection ES « {Collection} » : fichier present sans trace de propriete, laisse en place", nom);
            }
            else
            {
                File.Delete(chemin);
                fichierSupprime = true;
            }
        }

        var reglagesModifies = _settingsStore.Update(document =>
            EsCustomCollectionSettings.Remove(EnsureRoot(document), nom));

        var etatPath = StatePath(nom);
        if (File.Exists(etatPath))
        {
            File.Delete(etatPath);
        }

        return new EsCustomCollectionResult(fichierSupprime, reglagesModifies, 0);
    }

    /// <summary>Vrai si le fichier present est bien celui qu'APIExpose a ecrit.</summary>
    public bool Owns(string collectionName)
    {
        var nom = NormalizeName(collectionName);
        var etat = ReadState(nom);
        if (etat?.ContentSha256 is not { } empreinte)
        {
            return false;
        }

        var chemin = ConfigPath(nom);
        return File.Exists(chemin) &&
            string.Equals(Sha256(File.ReadAllText(chemin)), empreinte, StringComparison.OrdinalIgnoreCase);
    }

    public EsCustomCollectionState? ReadState(string collectionName)
    {
        var chemin = StatePath(NormalizeName(collectionName));
        if (!File.Exists(chemin))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EsCustomCollectionState>(File.ReadAllText(chemin));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Etat de collection illisible : {Chemin}", chemin);
            return null;
        }
    }

    /// <summary>
    /// Les chemins tels qu'ES les attend : separateur « / », sans doublon, tries. Le tri rend
    /// l'ecriture deterministe, donc l'empreinte comparable d'une synchronisation a l'autre.
    /// </summary>
    internal static List<string> NormalizePaths(IEnumerable<string> gamePaths)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lignes = new List<string>();
        foreach (var brut in gamePaths ?? Enumerable.Empty<string>())
        {
            var chemin = (brut ?? string.Empty).Trim().Replace('\\', '/');
            if (chemin.Length == 0 || !vus.Add(chemin))
            {
                continue;
            }

            lignes.Add(chemin);
        }

        lignes.Sort(StringComparer.OrdinalIgnoreCase);
        return lignes;
    }

    internal static string NormalizeName(string collectionName)
        => (collectionName ?? string.Empty).Trim().Trim('/', '\\');

    private static XElement EnsureRoot(XDocument document)
    {
        if (document.Root != null)
        {
            return document.Root;
        }

        var root = new XElement("config");
        document.Add(root);
        return root;
    }

    /// <summary>
    /// ES peut lire le fichier a tout instant : on ecrit a cote puis on remplace d'un bloc,
    /// pour qu'il ne voie jamais une liste a moitie ecrite.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var dossier = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dossier);
        var temporaire = Path.Combine(dossier, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(temporaire, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(temporaire, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporaire); } catch { }
            throw;
        }
    }

    private void WriteState(string collectionName, EsCustomCollectionState state)
    {
        Directory.CreateDirectory(_stateRoot);
        File.WriteAllText(StatePath(collectionName), JsonSerializer.Serialize(state, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Sha256(string contenu)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contenu))).ToLowerInvariant();
}

/// <summary>Ce qu'une ecriture a reellement change : rien de plus ne doit declencher un reload ES.</summary>
public readonly record struct EsCustomCollectionResult(bool FileChanged, bool SettingsChanged, int GameCount)
{
    public static EsCustomCollectionResult Unchanged => new(false, false, 0);

    public bool Changed => FileChanged || SettingsChanged;
}

/// <summary>La trace de propriete : sans elle, APIExpose ne touche pas a un fichier de collection.</summary>
public sealed class EsCustomCollectionState
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("content_sha256")]
    public string? ContentSha256 { get; set; }

    [JsonPropertyName("game_count")]
    public int GameCount { get; set; }

    [JsonPropertyName("listed_in_settings")]
    public bool ListedInSettings { get; set; }

    [JsonPropertyName("written_at_utc")]
    public DateTime WrittenAtUtc { get; set; }
}

/// <summary>
/// Les deux seules retouches autorisees dans <c>es_settings.cfg</c> pour une collection :
/// ajouter son nom, retirer son nom. L'ordre et les valeurs de l'utilisateur sont conserves.
/// </summary>
public static class EsCustomCollectionSettings
{
    public const string Key = "CollectionSystemsCustom";

    public static bool Add(XElement root, string value) => Upsert(root, Key, value);

    public static bool Remove(XElement root, string value) => RemoveFromList(root, Key, value);

    /// <summary>
    /// Ajoute une valeur a une liste separee par des virgules, sans doublon et sans toucher
    /// aux autres valeurs. Utilise aussi par l'installeur de packs de collections.
    /// </summary>
    public static bool Upsert(XElement root, string key, string value)
    {
        var existing = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            root.Add(new XText(Environment.NewLine + "  "));
            root.Add(new XElement("string", new XAttribute("name", key), new XAttribute("value", value)));
            return true;
        }

        existing.Name = "string";
        var current = existing.Attribute("value")?.Value ?? string.Empty;
        var values = current
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var matchingExisting = values
            .Where(existingValue => string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingExisting.Count == 1 && string.Equals(matchingExisting[0], value, StringComparison.Ordinal))
        {
            return false;
        }

        values.RemoveAll(existingValue => string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase));
        values.Add(value);
        existing.SetAttributeValue("value", string.Join(",", values));
        return true;
    }

    /// <summary>
    /// Retire une seule valeur de la liste. La cle n'est effacee que si elle devient vide,
    /// et jamais si elle contient encore des collections de l'utilisateur.
    /// </summary>
    public static bool RemoveFromList(XElement root, string key, string value)
    {
        var existing = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Attribute("name")?.Value, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return false;
        }

        var values = (existing.Attribute("value")?.Value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var retires = values.RemoveAll(existingValue =>
            string.Equals(existingValue, value, StringComparison.OrdinalIgnoreCase));
        if (retires == 0)
        {
            return false;
        }

        if (values.Count == 0)
        {
            existing.Remove();
            return true;
        }

        existing.SetAttributeValue("value", string.Join(",", values));
        return true;
    }
}
