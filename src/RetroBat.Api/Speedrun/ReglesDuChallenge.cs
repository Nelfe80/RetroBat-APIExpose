using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// LES REGLES D'UN CHALLENGE (« rules », wrapper 0.346, 13/11) : « QUAND signal ALORS effet », evaluees par le wrapper avant
/// chaque image depuis le chargement de l'etat de depart, comme les verrous. Le bloc du reglement part tel quel (JSON compact,
/// une seule ligne) dans certified.txt sous « rules= » ; le wrapper le compile, l'atteste (texte et empreinte) et rend au bilan
/// son empreinte et le compte des declenchements de chaque regle. L'API ne l'interprete pas : elle le transporte, en prouve
/// l'empreinte, et verifie qu'aucune ecriture ne touche une adresse que le reglement lit (depart, arrivee, annulation), la
/// meme limite que pour un verrou.
/// </summary>
public static class ReglesDuChallenge
{
    /// <summary>La premiere version du wrapper qui sait jouer des regles.</summary>
    public const int VersionMinimale = 346;

    /// <summary>La taille au-dela de laquelle un bloc est refuse (certified.txt reste un petit fichier lu au chargement).</summary>
    public const int TailleMaximale = 65536;

    private static readonly JsonWriterOptions Compact = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Le bloc en JSON compact, sur une ligne (les fins de ligne d'une chaine sont echappees par l'ecrivain).</summary>
    public static string Compacter(JsonElement bloc)
    {
        using var flux = new MemoryStream();
        using (var ecrivain = new Utf8JsonWriter(flux, Compact)) bloc.WriteTo(ecrivain);
        return Encoding.UTF8.GetString(flux.ToArray());
    }

    /// <summary>L'empreinte SHA-256 (hexadecimal minuscule) du texte transporte, celle que le wrapper rend ; « » sans regle.</summary>
    public static string Empreinte(string? texte)
        => string.IsNullOrEmpty(texte) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texte))).ToLowerInvariant();

    /// <summary>
    /// Les plages que les regles peuvent ecrire : chaque « write » (adresse, taille) et, s'il y a des « slot_write », toute la
    /// table des objets. Une adresse ecrite qui n'est pas un litteral (constante inconnue) rend une erreur : on ne promet pas ce
    /// qu'on ne sait pas borner.
    /// </summary>
    public static IReadOnlyList<(long De, long A)> PlagesEcrites(JsonElement bloc)
    {
        var constantes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (bloc.TryGetProperty("consts", out var cs) && cs.ValueKind == JsonValueKind.Object)
            foreach (var p in cs.EnumerateObject()) constantes[p.Name] = Litteral(p.Value, constantes);
        var plages = new List<(long, long)>();
        var ecritDansLesObjets = false;
        void Parcourir(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Parcourir(x); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("write", out var w) && w.ValueKind == JsonValueKind.String)
            {
                var taille = w.GetString() switch { "u8" => 1, "u16" => 2, "u32" => 4, _ => throw new ReglementInvalideException($"rules : write {w.GetString()}") };
                if (!e.TryGetProperty("address", out var a)) throw new ReglementInvalideException("rules : write sans address");
                var adresse = Litteral(a, constantes);
                plages.Add((adresse, adresse + taille - 1));
            }
            if (e.TryGetProperty("slot_write", out _)) ecritDansLesObjets = true;
            foreach (var p in e.EnumerateObject()) Parcourir(p.Value);
        }
        Parcourir(bloc);
        if (ecritDansLesObjets)
        {
            if (!bloc.TryGetProperty("objects", out var o) || o.ValueKind != JsonValueKind.Object)
                throw new ReglementInvalideException("rules : slot_write sans bloc objects");
            var base_ = Litteral(o.GetProperty("base"), constantes);
            var pas = Litteral(o.GetProperty("stride"), constantes);
            var nombre = Litteral(o.GetProperty("count"), constantes);
            plages.Add((base_, base_ + pas * nombre - 1));
        }
        return plages;
    }

    private static long Litteral(JsonElement e, IReadOnlyDictionary<string, long> constantes)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetInt64();
        if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var h)) return h;
            if (long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            if (constantes.TryGetValue(s, out var c)) return c;
        }
        throw new ReglementInvalideException($"rules : adresse ou nombre illisible ({e.GetRawText()})");
    }
}
