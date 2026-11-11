using System.Globalization;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// OU LIRE UNE ADRESSE DU .MEM PAR READ_CORE_RAM (11/11). READ_CORE_RAM, sans succes charges, lit la memoire systeme du coeur a
/// partir de 0. Les .MEM console y sont deja (index bruts). Les .MEM d'arcade portent des adresses MAME natives (Metal Slug 3
/// 0x10ED18, 19XX 0xFF8306), que le wrapper traduit pour FBNeo (wrapper.cpp, ReadMemoryValue) : par la table des banques du jeu
/// (resources\ram\arcade\mame-banks.json), sinon par repli dans le bloc de 64 Ko, mots de 16 bits inverses (RAM des 68000 de
/// FBNeo : octet MAME A a l'index A ^ 1). On fait la meme chose, en plus etroit : la table, la RAM de travail CPS (0xFF0000)
/// et Neo-Geo (0x100000), les index bruts ; ailleurs, rien (le film reste « non verifie » plutot qu'un faux « desync »).
/// Mesure : Metal Slug 3, 0x10ED18 lu a 0xED18 mots inverses, 33 220 (1LC certifie) relu dans le replay du 12/10.
/// </summary>
public static class AdressesDuCoeur
{
    public sealed record Banque(long De, long Taille, long Base, bool Mots16);

    private static readonly object Verrou = new();
    private static Dictionary<string, IReadOnlyList<Banque>>? _tables;

    /// <summary>Le coeur range-t-il la RAM d'arcade comme FBNeo ? Pur.</summary>
    public static bool FamilleFbneo(string? coeur)
    {
        var c = (coeur ?? "").ToLowerInvariant();
        return c.Contains("fbneo", StringComparison.Ordinal) || c.Contains("fbalpha", StringComparison.Ordinal);
    }

    /// <summary>
    /// Les index, dans la memoire que lit READ_CORE_RAM, des <paramref name="octets"/> octets de l'adresse MAME
    /// <paramref name="adresse"/>, dans l'ordre de l'adresse ; null si on ne sait pas ou les lire. Pur sur ses banques.
    /// </summary>
    public static long[]? Places(string? coeur, long adresse, int octets, IReadOnlyList<Banque>? banques)
    {
        if (octets <= 0) return null;
        if (!FamilleFbneo(coeur))
            return Enumerable.Range(0, octets).Select(i => adresse + i).ToArray();
        foreach (var b in banques ?? [])
        {
            if (adresse < b.Base || adresse + octets > b.Base + b.Taille) continue;
            var debut = b.De + (adresse - b.Base);
            return Enumerable.Range(0, octets).Select(i => b.Mots16 ? (debut + i) ^ 1 : debut + i).ToArray();
        }
        // Le repli du wrapper, seulement sur les deux RAM de travail de 64 Ko qu'il prouve (CPS, Neo-Geo) : mots inverses.
        if ((adresse >= 0xFF0000 && adresse + octets <= 0x1000000) || (adresse >= 0x100000 && adresse + octets <= 0x110000))
        {
            var debut = adresse & 0xFFFF;
            return Enumerable.Range(0, octets).Select(i => (debut + i) ^ 1).ToArray();
        }
        // Un index brut (releve sur le coeur lui-meme, Battle Garegga 0x166C) : tel quel.
        return adresse < 0x10000 ? Enumerable.Range(0, octets).Select(i => adresse + i).ToArray() : null;
    }

    /// <summary>Les banques d'un jeu d'arcade (table du Data Pack), vides sans entree.</summary>
    public static IReadOnlyList<Banque> BanquesDe(string? romGroup)
    {
        if (string.IsNullOrWhiteSpace(romGroup)) return [];
        lock (Verrou)
        {
            _tables ??= LireLesTables(Path.Combine(RetroBatPaths.PluginRoot, "resources", "ram", "arcade", "mame-banks.json"));
            return _tables.TryGetValue(romGroup.Trim(), out var b) ? b : [];
        }
    }

    internal static Dictionary<string, IReadOnlyList<Banque>> LireLesTables(string fichier)
    {
        var tables = new Dictionary<string, IReadOnlyList<Banque>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(fichier)) return tables;
            using var doc = JsonDocument.Parse(File.ReadAllText(fichier));
            foreach (var jeu in doc.RootElement.EnumerateObject())
            {
                if (jeu.Value.ValueKind != JsonValueKind.Array) continue;
                var banques = new List<Banque>();
                foreach (var b in jeu.Value.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Object) continue;
                    if (Nombre(b, "from") is not long de || Nombre(b, "size") is not long taille || Nombre(b, "base") is not long baseMame) continue;
                    var mots = b.TryGetProperty("swap16", out var s) && (s.ValueKind == JsonValueKind.True
                        || (s.ValueKind == JsonValueKind.Number && s.GetInt32() != 0));
                    banques.Add(new Banque(de, taille, baseMame, mots));
                }
                if (banques.Count > 0) tables[jeu.Name] = banques;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return tables;
    }

    private static long? Nombre(JsonElement e, string nom)
    {
        if (!e.TryGetProperty(nom, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind != JsonValueKind.String) return null;
        var t = v.GetString()!.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null
            : long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
