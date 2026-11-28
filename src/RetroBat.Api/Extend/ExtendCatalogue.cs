using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Extend;

/// <summary>
/// EXTEND MODE (28/11) : le catalogue des conversions, <c>extend/catalogue.json</c> du Data Pack (CDC Extend Mode v0.1, partie B).
/// Une fiche par ROM derivee : la source exacte (empreintes), le patch (IPS ou BPS, sous <c>extend/patches/</c>), le resultat
/// attendu et le coeur qui le joue. Le Data Pack ne porte JAMAIS la ROM derivee : seulement ce patch.
/// </summary>
public sealed class ExtendCatalogue
{
    public const string Schema = "nelfeplay.extend/1";
    public const string DossierDesPatchs = "extend/patches/";
    public const long PlafondPatch = 64L * 1024 * 1024;
    public const long PlafondResultat = 128L * 1024 * 1024;

    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Md5 = new("^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Crc = new("^[0-9a-f]{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Systeme = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NomDeCoeur = new("^[a-z0-9][a-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [JsonPropertyName("schema")] public string? SchemaLu { get; set; }

    [JsonPropertyName("conversions")] public List<ExtendConversion> Conversions { get; set; } = [];

    /// <summary>
    /// Lit et valide un catalogue. Un schema inconnu ou un JSON illisible refuse tout (null et <paramref name="erreur"/>) ; une
    /// fiche invalide est ecartee SEULE, sa raison dans <paramref name="ecartees"/> (une fiche abimee ne prive pas les autres).
    /// </summary>
    public static ExtendCatalogue? Lire(Stream flux, Version api, out string? erreur, out List<string> ecartees)
    {
        ecartees = [];
        ExtendCatalogue? brut;
        try
        {
            brut = JsonSerializer.Deserialize<ExtendCatalogue>(flux, Lecture);
        }
        catch (JsonException ex)
        {
            erreur = "catalogue Extend illisible : " + ex.Message;
            return null;
        }

        if (brut is null || !string.Equals(brut.SchemaLu, Schema, StringComparison.Ordinal))
        {
            erreur = "schema du catalogue Extend inconnu (attendu " + Schema + ")";
            return null;
        }

        var retenues = new List<ExtendConversion>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fiche in brut.Conversions ?? [])
        {
            var raison = Valider(fiche, api);
            if (raison is null && !ids.Add(fiche.Id))
            {
                raison = "identifiant en double";
            }

            if (raison is null)
            {
                retenues.Add(fiche);
            }
            else
            {
                ecartees.Add((fiche?.Id ?? "(sans id)") + " : " + raison);
            }
        }

        erreur = null;
        return new ExtendCatalogue { SchemaLu = brut.SchemaLu, Conversions = retenues };
    }

    /// <summary>La raison pour laquelle une fiche est refusee, ou null si elle est saine.</summary>
    public static string? Valider(ExtendConversion? f, Version api)
    {
        if (f is null)
        {
            return "fiche vide";
        }

        if (string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 120)
        {
            return "id absent";
        }

        if (f.System is null || !Systeme.IsMatch(f.System))
        {
            return "systeme invalide";
        }

        if (string.IsNullOrWhiteSpace(f.Title) || string.IsNullOrWhiteSpace(f.Version))
        {
            return "titre ou version absents";
        }

        if (f.Source is null || f.Source.Sha256 is null || !Sha256.IsMatch(f.Source.Sha256) || f.Source.Size <= 0 || f.Source.Size > PlafondResultat)
        {
            return "source : sha256 ou taille invalides";
        }

        if (f.Source.Md5 is { Length: > 0 } md5 && !Md5.IsMatch(md5) || f.Source.Crc is { Length: > 0 } crc && !Crc.IsMatch(crc))
        {
            return "source : md5 ou crc invalide";
        }

        if (f.Patch is null || f.Patch.Sha256 is null || !Sha256.IsMatch(f.Patch.Sha256) || f.Patch.Size <= 0 || f.Patch.Size > PlafondPatch)
        {
            return "patch : sha256 ou taille invalides (plafond 64 Mo)";
        }

        if (!string.Equals(f.Patch.Format, "ips", StringComparison.Ordinal) && !string.Equals(f.Patch.Format, "bps", StringComparison.Ordinal))
        {
            return "patch : format inconnu (ips ou bps)";
        }

        if (!CheminDePatchSur(f.Patch.Path))
        {
            return "patch : chemin hors de " + DossierDesPatchs;
        }

        if (f.Result is null || f.Result.Sha256 is null || !Sha256.IsMatch(f.Result.Sha256) || f.Result.Size <= 0 || f.Result.Size > PlafondResultat)
        {
            return "resultat : sha256 ou taille invalides (plafond 128 Mo)";
        }

        if (!NomDeFichierSur(f.Result.File))
        {
            return "resultat : nom de fichier invalide";
        }

        if (f.Core is null || f.Core.Name is null || !NomDeCoeur.IsMatch(f.Core.Name))
        {
            return "coeur : nom invalide";
        }

        if (f.Core.Sha256 is { } empreintes && empreintes.Any(e => e is null || !Sha256.IsMatch(e)))
        {
            return "coeur : empreinte invalide";
        }

        if (!string.IsNullOrWhiteSpace(f.MinApi) && Version.TryParse(f.MinApi, out var min) && min > api)
        {
            return "demande l'API " + f.MinApi + " ou plus";
        }

        return null;
    }

    /// <summary>Un chemin du Data Pack sous <c>extend/patches/</c> : relatif, sans remontee, sans separateur Windows ni caractere interdit.</summary>
    public static bool CheminDePatchSur(string? chemin)
    {
        if (string.IsNullOrEmpty(chemin) || chemin.Length > 300 || !chemin.StartsWith(DossierDesPatchs, StringComparison.Ordinal)
            || chemin.Length == DossierDesPatchs.Length || chemin.Contains('\\') || chemin.Contains(':'))
        {
            return false;
        }

        foreach (var segment in chemin.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Un simple nom de fichier : ni dossier, ni remontee, ni nom reserve, ni extension d'archive ou de script.</summary>
    public static bool NomDeFichierSur(string? nom)
    {
        if (string.IsNullOrWhiteSpace(nom) || nom.Length > 150 || nom != nom.Trim() || nom.StartsWith('.') || nom.EndsWith('.')
            || nom.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || nom.Contains('/') || nom.Contains('\\'))
        {
            return false;
        }

        var extension = Path.GetExtension(nom).ToLowerInvariant();
        return extension.Length > 1 && extension is not (".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".lnk" or ".xml" or ".json" or ".zip" or ".7z");
    }

    /// <summary>Le chemin local d'un patch (sous <c>resources/</c>), ou null s'il sortait de <c>extend/patches</c>.</summary>
    public static string? CheminLocalDuPatch(string racineResources, string cheminDuPatch)
    {
        if (!CheminDePatchSur(cheminDuPatch))
        {
            return null;
        }

        var confinement = Path.GetFullPath(Path.Combine(racineResources, "extend", "patches")) + Path.DirectorySeparatorChar;
        var complet = Path.GetFullPath(Path.Combine(racineResources, cheminDuPatch.Replace('/', Path.DirectorySeparatorChar)));
        return complet.StartsWith(confinement, StringComparison.OrdinalIgnoreCase) ? complet : null;
    }
}

public sealed class ExtendConversion
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("system")] public string? System { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("technology")] public string? Technology { get; set; }
    [JsonPropertyName("source")] public ExtendSource? Source { get; set; }
    [JsonPropertyName("patch")] public ExtendPatch? Patch { get; set; }
    [JsonPropertyName("result")] public ExtendResultat? Result { get; set; }
    [JsonPropertyName("core")] public ExtendCoeur? Core { get; set; }
    [JsonPropertyName("min_api")] public string? MinApi { get; set; }
}

public sealed class ExtendSource
{
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("crc")] public string? Crc { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class ExtendPatch
{
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ExtendResultat
{
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
}

public sealed class ExtendCoeur
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sha256")] public List<string>? Sha256 { get; set; }
}
