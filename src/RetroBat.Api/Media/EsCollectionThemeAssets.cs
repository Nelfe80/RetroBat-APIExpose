using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

/// <summary>
/// Pose l'identite visuelle d'une collection geree par APIExpose dans les themes installes :
/// la declaration dans <c>collections.info</c>, le logo, sa variante blanche et le fond.
///
/// Sans la declaration, EmulationStation ne donne PAS de tuile propre a la collection : au
/// reglage par defaut de <c>UseCustomCollectionsSystemEx</c>, il la range dans l'entree
/// fourre-tout « collections », ce qui annule tout l'interet de la collection.
///
/// Tout ce qui est depose est inscrit dans un manifeste avec son empreinte. Un fichier deja
/// present qui n'est pas le notre n'est jamais ecrase, et le retrait ne supprime que ce que
/// nous avons pose et qui n'a pas ete retouche depuis.
/// </summary>
public sealed class EsCollectionThemeAssets
{
    private const string EnteteDeclaration = "# Ajoute par APIExpose";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Dossiers de logos connus, par famille de theme.</summary>
    private static readonly string[][] DossiersLogos =
    [
        ["art", "logos", "collections"],                    // Carbon et derives
        ["_systemmedia", "_logosyst", "clearlogos"],         // HyperBat
    ];

    /// <summary>Dossiers de fonds connus, dans le meme ordre.</summary>
    private static readonly string[][] DossiersFonds =
    [
        ["art", "background", "collections"],
        ["_systemmedia", "_fanartsysteme", "fresh"],
    ];

    private readonly ILogger<EsCollectionThemeAssets>? _logger;
    private readonly string _themesRoot;
    private readonly string _sourceRoot;
    private readonly string _stateRoot;

    public EsCollectionThemeAssets(
        ILogger<EsCollectionThemeAssets>? logger = null,
        string? themesRoot = null,
        string? sourceRoot = null,
        string? stateRoot = null)
    {
        _logger = logger;
        _themesRoot = themesRoot ?? RetroBatPaths.EmulationStationThemesRoot;
        _sourceRoot = sourceRoot ?? Path.Combine(RetroBatPaths.ThemeResourcesRoot, "images");
        _stateRoot = stateRoot ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay");
    }

    /// <summary>Le logo couleur, sa variante blanche et le fond, livres par le Data Pack.</summary>
    public string LogoSource => Path.Combine(_sourceRoot, "nelfeplay-worldscoring.svg");

    public string LogoBlancSource => Path.Combine(_sourceRoot, "nelfeplay-worldscoring-white.svg");

    public string FondSource => Path.Combine(_sourceRoot, "nelfeplay-worldscoring-bg.jpg");

    public string ManifestePath(string collectionName)
        => Path.Combine(_stateRoot, "collection-assets-" + collectionName.Trim() + ".json");

    /// <summary>
    /// Depose les assets dans chaque theme installe qui sait les lire. Rien n'est recopie si
    /// la source n'a pas change : le deploiement ne doit pas provoquer un reload a chaque
    /// demarrage.
    /// </summary>
    public EsCollectionAssetsResult Install(string collectionName)
    {
        var nom = collectionName.Trim();
        if (nom.Length == 0 || !Directory.Exists(_themesRoot))
        {
            return EsCollectionAssetsResult.Rien;
        }

        var manifeste = LireManifeste(nom) ?? new EsCollectionAssetsManifest { Collection = nom };
        var deposes = manifeste.Files.ToDictionary(fichier => fichier.Path, StringComparer.OrdinalIgnoreCase);
        var declares = new HashSet<string>(manifeste.Declarations, StringComparer.OrdinalIgnoreCase);
        var change = false;

        var voulus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in Directory
                     .EnumerateDirectories(_themesRoot)
                     .OrderBy(chemin => chemin, StringComparer.OrdinalIgnoreCase))
        {
            change |= Declarer(theme, nom, declares);
            // UNIQUEMENT le logo couleur. Carbon cherche « <nom>.svg » puis « <nom>-w.svg » et
            // c'est le dernier trouve qui l'emporte : deposer la variante blanche ferait perdre
            // la marque, or et violet, sur le fond sombre du theme. La variante monochrome reste
            // livree par le Data Pack pour les surfaces qui en ont besoin.
            change |= Deposer(theme, DossiersLogos, nom + ".svg", LogoSource, deposes, voulus);
            change |= Deposer(theme, DossiersFonds, nom + ".jpg", FondSource, deposes, voulus);
        }

        // Un fichier qu'une version precedente deposait et qui n'est plus voulu s'en va, a
        // condition d'etre reste tel que nous l'avions pose.
        foreach (var obsolete in deposes.Keys.Where(chemin => !voulus.Contains(chemin)).ToList())
        {
            if (Retirer(deposes[obsolete]))
            {
                deposes.Remove(obsolete);
                change = true;
            }
        }

        if (change)
        {
            EcrireManifeste(nom, new EsCollectionAssetsManifest
            {
                Collection = nom,
                Files = deposes.Values.OrderBy(fichier => fichier.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                Declarations = declares.OrderBy(chemin => chemin, StringComparer.OrdinalIgnoreCase).ToList(),
            });
            _logger?.LogInformation(
                "Identite visuelle de la collection « {Collection} » : {Fichiers} fichiers, {Declarations} themes declares",
                nom, deposes.Count, declares.Count);
        }

        return new EsCollectionAssetsResult(change, deposes.Count, declares.Count);
    }

    /// <summary>Retire ce que nous avons pose, et rien d'autre.</summary>
    public EsCollectionAssetsResult Remove(string collectionName)
    {
        var nom = collectionName.Trim();
        var manifeste = LireManifeste(nom);
        if (nom.Length == 0 || manifeste == null)
        {
            return EsCollectionAssetsResult.Rien;
        }

        var change = false;
        foreach (var fichier in manifeste.Files)
        {
            change |= Retirer(fichier);
        }

        foreach (var declaration in manifeste.Declarations)
        {
            change |= RetirerDeclaration(declaration, nom);
        }

        try
        {
            if (File.Exists(ManifestePath(nom)))
            {
                File.Delete(ManifestePath(nom));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new EsCollectionAssetsResult(change, 0, 0);
    }

    /// <summary>Supprime un fichier que nous avons pose, sauf s'il a ete retouche depuis.</summary>
    private bool Retirer(EsCollectionAssetFile fichier)
    {
        try
        {
            if (!File.Exists(fichier.Path))
            {
                return false;
            }

            if (!string.Equals(Empreinte(fichier.Path), fichier.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Quelqu'un l'a retouche depuis : c'est devenu son fichier.
                _logger?.LogInformation("Asset de collection modifie depuis son depot, laisse en place : {Chemin}", fichier.Path);
                return false;
            }

            File.Delete(fichier.Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Asset de collection non supprime : {Chemin}", fichier.Path);
            return false;
        }
    }

    /// <summary>
    /// Ajoute le nom de la collection a <c>collections.info</c> du theme, en fin de fichier et
    /// sans toucher a une seule ligne existante. L'absence du fichier n'est pas une erreur :
    /// le theme ne gere simplement pas les collections personnalisees.
    /// </summary>
    private bool Declarer(string themeRoot, string collectionName, HashSet<string> declares)
    {
        var chemin = Path.Combine(themeRoot, "collections.info");
        if (!File.Exists(chemin))
        {
            _logger?.LogDebug("Theme sans collections.info, rendu generique : {Theme}", Path.GetFileName(themeRoot));
            return false;
        }

        try
        {
            var lignes = File.ReadAllLines(chemin);
            if (lignes.Any(ligne => string.Equals(ligne.Trim(), collectionName, StringComparison.OrdinalIgnoreCase)))
            {
                return declares.Add(chemin);
            }

            var contenu = File.ReadAllText(chemin);
            var fin = contenu.EndsWith('\n') ? string.Empty : Environment.NewLine;
            File.AppendAllText(chemin, fin + EnteteDeclaration + Environment.NewLine + collectionName + Environment.NewLine);
            declares.Add(chemin);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "collections.info non modifie : {Chemin}", chemin);
            return false;
        }
    }

    private bool RetirerDeclaration(string chemin, string collectionName)
    {
        try
        {
            if (!File.Exists(chemin))
            {
                return false;
            }

            var lignes = File.ReadAllLines(chemin);
            var gardees = new List<string>(lignes.Length);
            var retire = false;
            foreach (var ligne in lignes)
            {
                if (string.Equals(ligne.Trim(), collectionName, StringComparison.OrdinalIgnoreCase))
                {
                    // L'en-tete que nous avions ajoute part avec sa ligne.
                    if (gardees.Count > 0 && gardees[^1].Trim() == EnteteDeclaration)
                    {
                        gardees.RemoveAt(gardees.Count - 1);
                    }

                    retire = true;
                    continue;
                }

                gardees.Add(ligne);
            }

            if (retire)
            {
                File.WriteAllLines(chemin, gardees);
            }

            return retire;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "collections.info non nettoye : {Chemin}", chemin);
            return false;
        }
    }

    /// <summary>
    /// Copie un asset dans le premier dossier que ce theme possede. Un fichier deja la et
    /// inconnu du manifeste appartient a l'utilisateur : on n'y touche pas.
    /// </summary>
    private bool Deposer(
        string themeRoot,
        IEnumerable<string[]> dossiersCandidats,
        string nomFichier,
        string source,
        Dictionary<string, EsCollectionAssetFile> deposes,
        HashSet<string> voulus)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        foreach (var segments in dossiersCandidats)
        {
            var dossier = Path.Combine(new[] { themeRoot }.Concat(segments).ToArray());
            if (!Directory.Exists(dossier))
            {
                continue;
            }

            var cible = Path.Combine(dossier, nomFichier);
            var empreinteSource = Empreinte(source);
            try
            {
                if (File.Exists(cible))
                {
                    if (!deposes.ContainsKey(cible))
                    {
                        _logger?.LogInformation("Asset de collection deja present et non gere, laisse en place : {Chemin}", cible);
                        return false;
                    }

                    voulus.Add(cible);
                    if (string.Equals(Empreinte(cible), empreinteSource, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                voulus.Add(cible);

                File.Copy(source, cible, overwrite: true);
                deposes[cible] = new EsCollectionAssetFile { Path = cible, Sha256 = empreinteSource };
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Asset de collection non depose : {Chemin}", cible);
                return false;
            }
        }

        return false;
    }

    public EsCollectionAssetsManifest? LireManifeste(string collectionName)
    {
        var chemin = ManifestePath(collectionName);
        try
        {
            return File.Exists(chemin)
                ? JsonSerializer.Deserialize<EsCollectionAssetsManifest>(File.ReadAllText(chemin))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogDebug(ex, "Manifeste d'assets illisible : {Chemin}", chemin);
            return null;
        }
    }

    private void EcrireManifeste(string collectionName, EsCollectionAssetsManifest manifeste)
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);
            File.WriteAllText(ManifestePath(collectionName), JsonSerializer.Serialize(manifeste, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Manifeste d'assets non ecrit pour {Collection}", collectionName);
        }
    }

    private static string Empreinte(string chemin)
    {
        using var flux = File.Open(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(flux)).ToLowerInvariant();
    }
}

public readonly record struct EsCollectionAssetsResult(bool Changed, int Files, int Declarations)
{
    public static EsCollectionAssetsResult Rien => new(false, 0, 0);
}

/// <summary>Ce qu'APIExpose a pose dans les themes, et donc ce qu'il peut retirer.</summary>
public sealed class EsCollectionAssetsManifest
{
    [JsonPropertyName("collection")]
    public string Collection { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<EsCollectionAssetFile> Files { get; set; } = [];

    /// <summary>Les <c>collections.info</c> ou notre nom a ete inscrit.</summary>
    [JsonPropertyName("declarations")]
    public List<string> Declarations { get; set; } = [];
}

public sealed class EsCollectionAssetFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
