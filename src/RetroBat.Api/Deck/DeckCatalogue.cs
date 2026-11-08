using System.Security.Cryptography;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Deck;

/// <summary>
/// LES DECKS INSTALLES SUR LA BORNE (CDC PlayerDeck H-D16, H-D19). Deux dossiers a cote de l'exe du PlayerDeck :
/// <c>themes/</c>, les Decks officiels livres avec lui, et <c>decks/</c>, ceux des joueurs (terrain prepare : rien ne les
/// installe encore). Meme format (« nelfeplay.deck-theme/1 »). L'identifiant d'un Deck est stable : un Deck de joueur qui
/// reprend celui d'un Deck officiel est ignore. Un Deck vaut pour un jeu par sa liste <c>match.roms</c> (nom de la ROM ou
/// rom_group) ; <c>deck.default</c> designe le Deck officiel choisi d'office (aujourd'hui Hangar pour 19XX et Rebel Iron
/// pour Metal Slug 3). Lecture seule.
/// </summary>
public sealed class DeckCatalogue
{
    public const string Format = "nelfeplay.deck-theme/1";

    /// <summary>Un Deck installe.</summary>
    public sealed record Entree(
        string Id,
        string Name,
        int Version,
        bool Official,
        bool Default,
        string? VariantOf,
        IReadOnlyList<string> Roms,
        string File,
        string Sha256);

    private readonly IReadOnlyList<Entree> _entrees;

    public DeckCatalogue(IReadOnlyList<Entree> entrees) => _entrees = entrees;

    public IReadOnlyList<Entree> Tous => _entrees;

    /// <summary>Le dossier de l'exe du PlayerDeck sur la borne : <c>plugins\NelfePlay-PlayerDeck\bin</c>.</summary>
    public static string DossierParDefaut
        => Path.Combine(Path.GetDirectoryName(RetroBatPaths.PluginRoot.TrimEnd('\\', '/')) ?? RetroBatPaths.PluginRoot, "NelfePlay-PlayerDeck", "bin");

    /// <summary>Lit les Decks installes a cote de l'exe (themes/ puis decks/). Un fichier illisible est ignore.</summary>
    public static DeckCatalogue Lire(string dossierDuDeck)
    {
        var entrees = new List<Entree>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sous, officiel) in new[] { ("themes", true), ("decks", false) })
        {
            var dossier = Path.Combine(dossierDuDeck, sous);
            if (!Directory.Exists(dossier)) continue;
            var fichiers = Directory.GetFiles(dossier, "*.json");
            Array.Sort(fichiers, StringComparer.OrdinalIgnoreCase);
            foreach (var fichier in fichiers)
            {
                if (LireUn(fichier, officiel) is not { } e || !ids.Add(e.Id)) continue;
                entrees.Add(e);
            }
        }

        return new DeckCatalogue(entrees);
    }

    /// <summary>Un fichier de Deck, ou null s'il n'en est pas un. L'identifiant est le nom du fichier (comme le Deck le lit).</summary>
    internal static Entree? LireUn(string fichier, bool officiel)
    {
        try
        {
            var octets = File.ReadAllBytes(fichier);
            using var doc = JsonDocument.Parse(octets, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var racine = doc.RootElement;
            if (racine.ValueKind != JsonValueKind.Object) return null;
            if (Texte(racine, "format") is { } format && !string.Equals(format, Format, StringComparison.Ordinal)) return null;

            var id = Path.GetFileNameWithoutExtension(fichier).ToLowerInvariant();
            var roms = new List<string>();
            string? variante = null;
            if (racine.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Object)
            {
                if (match.TryGetProperty("roms", out var r) && r.ValueKind == JsonValueKind.Array)
                    roms.AddRange(r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
                variante = Texte(match, "variantOf");
            }

            var defaut = racine.TryGetProperty("deck", out var deck) && deck.ValueKind == JsonValueKind.Object
                && deck.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True;
            var version = racine.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 1;
            return new Entree(id, Texte(racine, "name") ?? id, version, officiel, officiel && defaut, variante, roms, fichier,
                Convert.ToHexString(SHA256.HashData(octets)).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Les Decks d'un jeu, dans l'ordre du choix en jeu : le Deck officiel par defaut, les autres Decks officiels (le
    /// principal avant ses variantes), puis ceux des joueurs. Par le nom de la ROM, sinon par le rom_group.
    /// </summary>
    public IReadOnlyList<Entree> PourLeJeu(string? rom, string? romGroup)
    {
        var cles = new[] { rom, romGroup }.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim().ToLowerInvariant()).ToList();
        if (cles.Count == 0) return [];
        return _entrees
            .Where(e => cles.Any(c => e.Id == c || e.Roms.Contains(c)))
            .OrderBy(e => e.Official ? 0 : 1)
            .ThenBy(e => e.Default ? 0 : 1)
            .ThenBy(e => e.VariantOf is null ? 0 : 1)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Le Deck installe de cet identifiant, ou null.</summary>
    public Entree? Trouver(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : _entrees.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? Texte(JsonElement e, string nom)
        => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
