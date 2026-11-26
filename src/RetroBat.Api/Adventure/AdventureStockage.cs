using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Adventure;

/// <summary>
/// LA COPIE LOCALE DES VERSIONS, chez l'hote (CDC v1.0 §2, point 5) : dans le dossier de l'API
/// (<c>state\nelfeplay\adventure</c>), jamais dans les sauvegardes personnelles du joueur. Les objets (etat, sauvegarde de la
/// cartouche, capture) sont rangés par leur empreinte SHA-256, comme sur le site et comme les replays ; une version est un
/// petit fichier JSON qui les nomme.
///
/// <code>
/// adventure\objects\&lt;sha256&gt;          les octets
/// adventure\&lt;aventure&gt;\&lt;version&gt;.json  la version
/// adventure\saves\&lt;aventure&gt;\          le dossier de sauvegardes de RetroArch pour cette aventure (savefile_directory)
/// adventure\envoi.json                 la file d'envoi (AdventureFileDEnvoi)
/// </code>
/// </summary>
public sealed class AdventureStockage
{
    private static readonly JsonSerializerOptions Ecriture = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _verrou = new();

    public AdventureStockage(string? racine = null)
    {
        Racine = racine ?? Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfeplay", "adventure");
    }

    public string Racine { get; }

    public string DossierDesObjets => Path.Combine(Racine, "objects");

    /// <summary>Un identifiant venu du site ou d'un fichier : jamais un chemin (pas de separateur, pas de « .. »).</summary>
    public static bool NomSur(string? nom)
        => !string.IsNullOrEmpty(nom) && nom.Length <= 80 && nom.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !nom.Contains("..", StringComparison.Ordinal);

    public static string Empreinte(ReadOnlySpan<byte> octets) => Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();

    private static bool EmpreinteSure(string? sha) => sha is { Length: 64 } && sha.All(char.IsAsciiHexDigit);

    /// <summary>Le dossier de sauvegardes de RetroArch propre a cette aventure : celles du joueur n'y sont jamais.</summary>
    public string DossierDesSauvegardes(string aventure)
        => NomSur(aventure) ? Path.Combine(Racine, "saves", aventure) : throw new ArgumentException("identifiant d'aventure refuse", nameof(aventure));

    /// <summary>
    /// Le dossier des ETATS de RetroArch pour cette aventure (<c>savestate_directory</c> de la seance) : <c>SAVE_STATE</c> y ecrit
    /// <c>&lt;rom&gt;.state</c>, l'autosave <c>&lt;rom&gt;.state.auto</c>, le depart pret <c>&lt;rom&gt;.state97</c> ; jamais les etats du joueur.
    /// </summary>
    public string DossierDesEtats(string aventure)
        => NomSur(aventure) ? Path.Combine(Racine, "states", aventure) : throw new ArgumentException("identifiant d'aventure refuse", nameof(aventure));

    /// <summary>Range des octets sous leur empreinte (une fois) et la rend.</summary>
    public string PoserObjet(byte[] octets)
    {
        var sha = Empreinte(octets);
        var chemin = Path.Combine(DossierDesObjets, sha);
        lock (_verrou)
        {
            if (!File.Exists(chemin) || new FileInfo(chemin).Length != octets.Length)
            {
                Directory.CreateDirectory(DossierDesObjets);
                var temp = chemin + ".tmp";
                File.WriteAllBytes(temp, octets);
                File.Move(temp, chemin, overwrite: true);
            }
        }

        return sha;
    }

    /// <summary>Les octets d'un objet, relus et verifies contre leur empreinte ; null s'il manque ou ne correspond plus.</summary>
    public byte[]? LireObjet(string? sha)
    {
        if (!EmpreinteSure(sha))
        {
            return null;
        }

        var chemin = Path.Combine(DossierDesObjets, sha!.ToLowerInvariant());
        try
        {
            if (!File.Exists(chemin))
            {
                return null;
            }

            var octets = File.ReadAllBytes(chemin);
            return string.Equals(Empreinte(octets), sha, StringComparison.OrdinalIgnoreCase) ? octets : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool ADejaObjet(string? sha) => EmpreinteSure(sha) && File.Exists(Path.Combine(DossierDesObjets, sha!.ToLowerInvariant()));

    public void GarderVersion(AdventureVersion version)
    {
        if (!NomSur(version.Adventure) || !NomSur(version.Id))
        {
            throw new ArgumentException("identifiant refuse");
        }

        var dossier = Path.Combine(Racine, version.Adventure);
        lock (_verrou)
        {
            Directory.CreateDirectory(dossier);
            var chemin = Path.Combine(dossier, version.Id + ".json");
            File.WriteAllText(chemin + ".tmp", JsonSerializer.Serialize(version, Ecriture));
            File.Move(chemin + ".tmp", chemin, overwrite: true);
        }
    }

    /// <summary>Les versions gardees d'une aventure, la plus recente d'abord.</summary>
    public IReadOnlyList<AdventureVersion> Versions(string aventure)
    {
        if (!NomSur(aventure))
        {
            return [];
        }

        var dossier = Path.Combine(Racine, aventure);
        if (!Directory.Exists(dossier))
        {
            return [];
        }

        var versions = new List<AdventureVersion>();
        foreach (var fichier in Directory.EnumerateFiles(dossier, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<AdventureVersion>(File.ReadAllText(fichier), Ecriture) is { } v)
                {
                    versions.Add(v);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Une version illisible n'arrete rien : les autres restent.
            }
        }

        return versions.OrderByDescending(v => v.CreatedUtc).ToList();
    }

    public AdventureVersion? Derniere(string aventure) => Versions(aventure).FirstOrDefault();

    /// <summary>
    /// Un identifiant de version : 32 hex minuscules (le format du site). C'est aussi le <c>version_ref</c> du depot : le meme a chaque
    /// essai, donc jamais deux versions pour une meme prise, et l'ordre des versions se lit a leur date, pas a leur nom.
    /// </summary>
    public static string NouvelIdentifiant() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
