using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LE ✓1CC, ONE-CREDIT CLEAR (decision user du 2026-10-24) : le jeu FINI sur un seul credit, a cote du 1CC
/// (One-Credit Challenge, le score sur un credit). Un jeu qui boucle est fini a la fin de sa premiere boucle.
///
/// La fin se lit par une ligne EXISTANTE du .MEM, jamais un ajout a sa structure : le profil la cite dans
/// <c>clear</c> (<c>action</c>, et <c>address</c> / <c>value</c> quand plusieurs lignes portent l'action ; Sonic 1 :
/// LEVEL_ENDING a 0xFE11 = 6, la zone de la fin). La borne retient le premier signal conforme d'une partie jouee,
/// avant tout continue, et le passeport signe le porte (<c>progression.clear</c>) ; la plateforme le confronte
/// a la regle de son profil et marque le score. Un jeu sans fin n'a pas de regle.
/// </summary>
public static class FinDuJeu
{
    /// <summary>La regle d'un profil : l'action du .MEM, son adresse et sa valeur quand le profil les precise.</summary>
    public sealed record Regle(string Action, long? Adresse, int? Valeur);

    /// <summary>Le clear d'une partie : la frame du signal et le score du moment.</summary>
    public sealed record Clear(long Frame, long Score, Regle Regle);

    /// <summary>Une partie peut finir sans marquer (generique) : le signal arrive jusqu'a deux minutes apres la derniere lecture du run.</summary>
    public const long FramesApresLeRun = 7200;

    /// <summary>La regle <c>clear</c> d'un profil, ou null (pas de regle, ou une regle illisible).</summary>
    public static Regle? DuProfil(JsonElement? profil)
    {
        if (profil is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("clear", out var c) || c.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var action = c.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(action))
        {
            return null;
        }

        long? adresse = null;
        if (c.TryGetProperty("address", out var ad))
        {
            adresse = Hexa(ad.ValueKind == JsonValueKind.String ? ad.GetString() : null);
            if (adresse is null) return null;
        }

        int? valeur = null;
        if (c.TryGetProperty("value", out var v))
        {
            valeur = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
                : v.ValueKind == JsonValueKind.String && Hexa(v.GetString()) is { } h and <= int.MaxValue ? (int) h : null;
            if (valeur is null) return null;
        }

        return new Regle(action.ToUpperInvariant(), adresse, valeur);
    }

    /// <summary>Le signal du .MEM est-il celui de la regle ? Une adresse ou une valeur que le signal ne dit pas ne correspond pas.</summary>
    public static bool Correspond(Regle regle, string? nom, string? adresse, int? valeur)
        => string.Equals(nom?.Trim(), regle.Action, StringComparison.OrdinalIgnoreCase)
           && (regle.Adresse is null || Hexa(adresse) == regle.Adresse)
           && (regle.Valeur is null || valeur == regle.Valeur);

    /// <summary>
    /// Le clear appartient-il au run retenu ? Il tombe apres sa premiere lecture, au plus
    /// <see cref="FramesApresLeRun"/> apres sa derniere, et aucune lecture plus basse (une nouvelle partie)
    /// ne s'intercale entre la fin du run et lui.
    /// </summary>
    public static bool DansLeRun(Clear clear, IReadOnlyList<(long frame, long total)> run, IReadOnlyList<(long frame, long total)> lectures)
    {
        if (run.Count == 0 || clear.Frame < run[0].frame || clear.Frame > run[^1].frame + FramesApresLeRun)
        {
            return false;
        }

        var fin = run[^1];
        return !lectures.Any(l => l.frame > fin.frame && l.frame <= clear.Frame && l.total < fin.total);
    }

    /// <summary>Ce que le passeport signe en porte (progression.clear).</summary>
    public static JsonObject EnJson(Clear clear) => new()
    {
        ["frame"] = clear.Frame,
        ["score"] = clear.Score.ToString(CultureInfo.InvariantCulture),
        ["action"] = clear.Regle.Action,
        ["address"] = clear.Regle.Adresse is { } a ? "0x" + a.ToString("X", CultureInfo.InvariantCulture) : null,
        ["value"] = clear.Regle.Valeur,
    };

    /// <summary>Le clear garde dans un brouillon, ou null.</summary>
    public static Clear? DuJson(JsonNode? noeud)
    {
        if (noeud is not JsonObject o || (string?) o["action"] is not { Length: > 0 } action || (long?) o["frame"] is not { } frame)
        {
            return null;
        }

        var score = long.TryParse((string?) o["score"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return new Clear(frame, score, new Regle(action, Hexa((string?) o["address"]), (int?) o["value"]));
    }

    private static long? Hexa(string? texte)
    {
        var t = (texte ?? string.Empty).Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return t.Length > 0 && long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
