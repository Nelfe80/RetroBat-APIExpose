using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Avatar;

/// <summary>
/// Les PLANCHES D'AVATAR que cette borne detient.
///
/// Une planche est un objet NelfeNet comme un replay : immuable, rangee sous son empreinte, et
/// circulant de borne en borne par le relais. La plateforme n'en garde jamais les octets, seulement
/// l'index (pseudo, famille, version, variation) vers empreinte.
///
/// Magasin a part, et pas un dossier de plus dans celui des replays : tout y parle de manifeste, de
/// visibilite et d'extension `.replay`, et une planche n'a rien de tout ca. Seuls le recensement et
/// le relais connaissent les deux sortes d'objets.
///
/// Rien n'est cru sur parole : l'empreinte est RECALCULEE a l'import, et des octets qui n'ont pas la
/// forme d'une planche (PNG de 320 x 192) n'entrent pas. L'image n'est pas decodee ici : c'est le HUD
/// qui la dessine, et un PNG corrompu y echouera sans rien emporter.
/// </summary>
public sealed class AvatarSheetStore
{
    /// <summary>5 images (repos + 4 pas) par ligne, 3 vues (face, dos, profil droit), en 64 x 64.</summary>
    public const int Largeur = 320;
    public const int Hauteur = 192;
    public const int TailleSprite = 64;

    /// <summary>Une planche indexee pese quelques kilo-octets : au-dela, ce n'en est pas une.</summary>
    public const int OctetsMax = 256 * 1024;

    /// <summary>Au-dela, les planches les moins recemment dessinees partent. Quatre mille planches
    /// tiennent en une vingtaine de mega-octets.</summary>
    public const int Capacite = 4000;

    private static readonly byte[] SignaturePng = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly TimeSpan PasDeRetouche = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Ce que la borne sait d'une planche : a qui elle est. `Verifiee` dit si l'empreinte a ete
    /// confirmee par l'index de la plateforme ; une planche posee a la main pour un essai ne l'est
    /// pas, et n'est jamais annoncee au recensement.
    /// </summary>
    public sealed record Entree(string Pseudo, string Famille, int Variation, string Generateur, string Sha256, bool Verifiee);

    public sealed record Resultat(bool Ok, string Sha256, string? Erreur);

    private readonly string _objets, _temp, _catalogue;
    private readonly object _verrou = new();
    private readonly Dictionary<string, DateTime> _retouches = new(StringComparer.Ordinal);
    private readonly ILogger<AvatarSheetStore> _logger;
    private List<Entree>? _entrees;

    public AvatarSheetStore(ILogger<AvatarSheetStore> logger)
        : this(Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "avatar"), logger)
    {
    }

    public AvatarSheetStore(string racine, ILogger<AvatarSheetStore> logger)
    {
        _logger = logger;
        _objets = Path.Combine(racine, "objects", "sha256");
        _temp = Path.Combine(racine, "temp");
        _catalogue = Path.Combine(racine, "catalogue.json");
        Directory.CreateDirectory(_objets);
        Directory.CreateDirectory(_temp);
    }

    public static bool EstSha(string? s)
        => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string ObjectPath(string sha256) => Path.Combine(_objets, sha256[..2], sha256 + ".png");

    public bool Has(string? sha256) => EstSha(sha256) && File.Exists(ObjectPath(sha256!));

    /// <summary>Pourquoi des octets ne sont pas une planche, ou null s'ils en ont la forme.</summary>
    public static string? Valider(ReadOnlySpan<byte> octets)
    {
        if (octets.Length > OctetsMax) return "too_large";

        // Signature (8), longueur du premier bloc (4), son type (4), largeur (4), hauteur (4).
        if (octets.Length < 24 || !octets[..8].SequenceEqual(SignaturePng)) return "not_png";
        if (!octets.Slice(12, 4).SequenceEqual("IHDR"u8)) return "not_png";

        var largeur = BinaryPrimitives.ReadInt32BigEndian(octets.Slice(16, 4));
        var hauteur = BinaryPrimitives.ReadInt32BigEndian(octets.Slice(20, 4));
        return largeur == Largeur && hauteur == Hauteur ? null : "bad_dimensions";
    }

    /// <summary>
    /// Range des octets sous LEUR empreinte. Si l'appelant en annonce une, elle doit tomber juste :
    /// des octets qui ne correspondent pas a ce qu'on attendait ne sont pas gardes sous un autre nom.
    /// </summary>
    public async Task<Resultat> ImporterAsync(byte[] octets, string? shaAttendu, CancellationToken ct)
    {
        var erreur = Valider(octets);
        if (erreur is not null) return new Resultat(false, "", erreur);

        var sha = Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant();
        if (shaAttendu is not null && !string.Equals(sha, shaAttendu.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new Resultat(false, sha, "sha_mismatch");
        }

        var dest = ObjectPath(sha);
        if (!File.Exists(dest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = Path.Combine(_temp, sha + "." + Guid.NewGuid().ToString("N")[..8] + ".part");
            try
            {
                await File.WriteAllBytesAsync(tmp, octets, ct).ConfigureAwait(false);
                // Deux imports simultanes de la meme planche : le second trouve la place prise, et
                // c'est la meme chose puisque c'est la meme empreinte.
                if (!File.Exists(dest)) File.Move(tmp, dest, overwrite: false);
            }
            catch (IOException) when (File.Exists(dest))
            {
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            Elaguer();
        }

        return new Resultat(true, sha, null);
    }

    public async Task<Resultat> ImporterFichierAsync(string chemin, string? shaAttendu, CancellationToken ct)
    {
        var info = new FileInfo(chemin);
        if (!info.Exists) return new Resultat(false, "", "missing");
        if (info.Length > OctetsMax) return new Resultat(false, "", "too_large");
        var octets = await File.ReadAllBytesAsync(chemin, ct).ConfigureAwait(false);
        return await ImporterAsync(octets, shaAttendu, ct).ConfigureAwait(false);
    }

    /// <summary>Les planches presentes, par empreinte.</summary>
    public IReadOnlyList<string> Objets()
    {
        var sortie = new List<string>();
        if (!Directory.Exists(_objets)) return sortie;
        foreach (var f in Directory.EnumerateFiles(_objets, "*.png", SearchOption.AllDirectories))
        {
            var sha = Path.GetFileNameWithoutExtension(f);
            if (EstSha(sha)) sortie.Add(sha);
        }
        return sortie;
    }

    /// <summary>
    /// Les planches a DECLARER au recensement : toutes, sauf celles qu'on ne connait que par un
    /// essai local. Une planche venue du relais a ete demandee sous une empreinte lue dans l'index,
    /// une planche remise par le navigateur a ete confirmee contre lui : les deux sont sures.
    /// </summary>
    public IReadOnlyList<string> ADeclarer()
    {
        HashSet<string> essais;
        lock (_verrou)
        {
            var entrees = Charger();
            var sures = entrees.Where(e => e.Verifiee).Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);
            essais = entrees.Where(e => !e.Verifiee && !sures.Contains(e.Sha256))
                .Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);
        }
        return Objets().Where(sha => !essais.Contains(sha)).ToList();
    }

    /// <summary>Note a qui est une planche. Une nouvelle entree pour le meme quadruplet remplace l'ancienne.</summary>
    public void Cataloguer(string pseudo, string famille, int variation, string generateur, string sha256, bool verifiee)
    {
        lock (_verrou)
        {
            var liste = Charger();
            liste.RemoveAll(e => e.Pseudo == pseudo && e.Famille == famille
                                 && e.Variation == variation && e.Generateur == generateur);
            liste.Add(new Entree(pseudo, famille, variation, generateur, sha256, verifiee));
            if (liste.Count > Capacite) liste.RemoveRange(0, liste.Count - Capacite);
            Sauver(liste);
        }
    }

    /// <summary>
    /// La planche la plus RECEMMENT cataloguee pour (pseudo, famille, variation), si elle est encore
    /// la. Le pseudo se compare a l'octet pres, comme dans l'index : le generateur distingue
    /// « Nelfe80 » de « nelfe80 ».
    /// </summary>
    public Entree? Trouver(string pseudo, string famille, int variation)
    {
        lock (_verrou)
        {
            var liste = Charger();
            for (var i = liste.Count - 1; i >= 0; i--)
            {
                var e = liste[i];
                if (string.Equals(e.Pseudo, pseudo, StringComparison.Ordinal)
                    && string.Equals(e.Famille, famille, StringComparison.Ordinal)
                    && e.Variation == variation
                    && Has(e.Sha256))
                {
                    return e;
                }
            }
            return null;
        }
    }

    /// <summary>Une planche dessinee rajeunit : c'est ce qui la garde a l'elagage.</summary>
    public void Utiliser(string sha256)
    {
        if (!Has(sha256)) return;
        lock (_verrou)
        {
            var maintenant = DateTime.UtcNow;
            if (_retouches.TryGetValue(sha256, out var derniere) && maintenant - derniere < PasDeRetouche) return;
            _retouches[sha256] = maintenant;
        }
        try { File.SetLastWriteTimeUtc(ObjectPath(sha256), DateTime.UtcNow); } catch { }
    }

    /// <summary>Retire les planches les moins recemment dessinees au-dela de la capacite.</summary>
    public int Elaguer()
    {
        try
        {
            var fichiers = new DirectoryInfo(_objets).EnumerateFiles("*.png", SearchOption.AllDirectories).ToList();
            if (fichiers.Count <= Capacite) return 0;
            var retirees = 0;
            foreach (var f in fichiers.OrderBy(f => f.LastWriteTimeUtc).Take(fichiers.Count - Capacite))
            {
                try { f.Delete(); retirees++; } catch { }
            }
            _logger.LogInformation("Avatars : {Count} planche(s) elaguee(s).", retirees);
            return retirees;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Avatars : elagage impossible.");
            return 0;
        }
    }

    private List<Entree> Charger()
    {
        if (_entrees is not null) return _entrees;
        try
        {
            _entrees = File.Exists(_catalogue)
                ? JsonSerializer.Deserialize<List<Entree>>(File.ReadAllBytes(_catalogue), Json) ?? new List<Entree>()
                : new List<Entree>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Avatars : catalogue illisible, repart vide.");
            _entrees = new List<Entree>();
        }
        return _entrees;
    }

    private void Sauver(List<Entree> liste)
    {
        _entrees = liste;
        try
        {
            var tmp = _catalogue + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(liste, Json));
            File.Move(tmp, _catalogue, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Avatars : catalogue non ecrit.");
        }
    }
}
